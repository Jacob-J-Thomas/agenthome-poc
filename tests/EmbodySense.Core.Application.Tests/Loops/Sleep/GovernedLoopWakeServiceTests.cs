using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Sleep;

public sealed class GovernedLoopWakeServiceTests
{
    [Fact]
    public async Task Timestamp_wake_waits_for_deadline_without_claiming_checkpoint()
    {
        var harness = new GovernedLoopSleepApplicationHarness(deadlineUtc: GovernedLoopSleepApplicationTestFixture.Now.AddMinutes(1));
        var checkpoint = await harness.PublishAsync();

        var result = await harness.Service.WakeAsync(new GovernedLoopWakeRequest(checkpoint.CheckpointId, checkpoint.ContentHash));

        Assert.Equal(GovernedLoopWakeResultStatus.NotEligible, result.Status);
        Assert.Equal(0, harness.Store.WakeCount);
        Assert.Equal(0, harness.Continuation.ContinueCount);
    }

    [Fact]
    public async Task Timestamp_wake_rejects_clock_rollback_before_checkpoint_publication_even_after_deadline()
    {
        var harness = new GovernedLoopSleepApplicationHarness(
            deadlineUtc: GovernedLoopSleepApplicationTestFixture.Now.AddMinutes(-1));
        var checkpoint = await harness.PublishAsync();
        var rolledBackAtUtc = checkpoint.PublishedAtUtc.AddSeconds(-30);
        harness.TimeProvider.UtcNow = rolledBackAtUtc;
        harness.CurrentPosture.Result = new GovernedLoopSleepCurrentPostureReadResult(
            GovernedLoopSleepCurrentPostureReadStatus.Found,
            GovernedLoopSleepApplicationTestFixture.Posture(observedAtUtc: rolledBackAtUtc));

        var result = await harness.Service.WakeAsync(new GovernedLoopWakeRequest(checkpoint.CheckpointId, checkpoint.ContentHash));

        Assert.True(rolledBackAtUtc > checkpoint.WakeDeadlineUtc!.Value);
        Assert.Equal(GovernedLoopWakeResultStatus.NotEligible, result.Status);
        Assert.Equal(0, harness.Store.WakeCount);
        Assert.Equal(0, harness.Continuation.ContinueCount);
    }

    [Fact]
    public async Task Timestamp_wake_persists_prepared_before_exact_continuation_and_commits_once()
    {
        var harness = new GovernedLoopSleepApplicationHarness();
        var checkpoint = await harness.PublishAsync();
        GovernedLoopWakeDisposition? observed = null;
        harness.Continuation.OnContinue = (request, cancellationToken) =>
        {
            var stored = harness.Store.GetWake(request.Identity.WakeId);
            observed = stored.Disposition;
            Assert.False(cancellationToken.CanBeCanceled);
            Assert.Equal(harness.Posture.PostureHash, request.ExpectedPostureHash);
            Assert.NotSame(stored, request.PreparedWakeEvidence);
            Assert.NotSame(stored.Identity, request.PreparedWakeEvidence!.Identity);
            Assert.Equal(stored.ContentHash, request.PreparedWakeEvidence.ContentHash);
        };

        var result = await harness.Service.WakeAsync(new GovernedLoopWakeRequest(checkpoint.CheckpointId, checkpoint.ContentHash));

        Assert.Equal(GovernedLoopWakeResultStatus.Committed, result.Status);
        Assert.True(result.ContinuationInvoked);
        Assert.Equal(GovernedLoopWakeDisposition.Prepared, observed);
        Assert.Equal(GovernedLoopWakeDisposition.Committed, result.Evidence!.Disposition);
        Assert.Equal(2, result.Evidence.EvidenceVersion);
        Assert.Equal(1, harness.Continuation.CommittedOperationCount);
        Assert.Equal(0, harness.AuthenticatedWakeVerification.VerifyCount);
    }

    [Fact]
    public async Task Ambiguous_retry_carries_the_exact_retained_prepared_predecessor()
    {
        var harness = new GovernedLoopSleepApplicationHarness();
        var checkpoint = await harness.PublishAsync();
        harness.Continuation.ContinueException = new InvalidOperationException("simulated continuation crash");

        var first = await harness.Service.WakeAsync(new GovernedLoopWakeRequest(checkpoint.CheckpointId, checkpoint.ContentHash));
        var ambiguous = Assert.IsType<GovernedLoopWakeEvidence>(first.Evidence);
        var prepared = harness.Store.GetPreparedWake(ambiguous.Identity.WakeId);
        GovernedLoopWakeContinuationRequest? retried = null;
        harness.Continuation.ContinueException = null;
        harness.Continuation.OnContinue = (request, _) => retried = request;

        var recovered = await harness.Service.WakeAsync(new GovernedLoopWakeRequest(checkpoint.CheckpointId, checkpoint.ContentHash));

        Assert.Equal(GovernedLoopWakeResultStatus.AmbiguousAttempt, first.Status);
        Assert.Equal(GovernedLoopWakeDisposition.AmbiguousAttempt, ambiguous.Disposition);
        Assert.Equal(GovernedLoopWakeResultStatus.Committed, recovered.Status);
        Assert.NotNull(retried);
        Assert.Equal(prepared.ContentHash, retried.PreparedWakeEvidence?.ContentHash);
        Assert.NotEqual(ambiguous.ContentHash, retried.PreparedWakeEvidence?.ContentHash);
        Assert.Equal(2, harness.Continuation.ContinueCount);
        Assert.Equal(1, harness.Continuation.ReconcileCount);
    }

    [Fact]
    public async Task Wake_rejects_a_substituted_prepared_predecessor_before_reconciliation()
    {
        var harness = new GovernedLoopSleepApplicationHarness();
        var checkpoint = await harness.PublishAsync();
        harness.Continuation.ContinueException = new InvalidOperationException("simulated continuation crash");
        var first = await harness.Service.WakeAsync(new GovernedLoopWakeRequest(checkpoint.CheckpointId, checkpoint.ContentHash));
        var ambiguous = Assert.IsType<GovernedLoopWakeEvidence>(first.Evidence);
        var prepared = harness.Store.GetPreparedWake(ambiguous.Identity.WakeId);
        harness.Store.WakeReadOverride = new GovernedLoopWakeEvidenceReadResult(
            GovernedLoopSleepStoreReadStatus.Found,
            ambiguous,
            prepared with { ContentHash = GovernedLoopSleepApplicationTestFixture.Hash('9') });

        var rejected = await harness.Service.WakeAsync(new GovernedLoopWakeRequest(checkpoint.CheckpointId, checkpoint.ContentHash));

        Assert.Equal(GovernedLoopWakeResultStatus.Conflict, rejected.Status);
        Assert.Equal(1, harness.Continuation.ContinueCount);
        Assert.Equal(0, harness.Continuation.ReconcileCount);
    }

    [Fact]
    public async Task Wake_remains_eligible_when_sibling_progress_advances_frontier()
    {
        var waiting = GovernedLoopSleepApplicationTestFixture.WaitingNode();
        var publishedPosture = GovernedLoopSleepApplicationTestFixture.Posture(
            node: waiting,
            lifecycleStatus: GovernedLoopRunStatus.Running,
            frontierStatus: GovernedLoopFrontierStatus.Active,
            nodes: [waiting, GovernedLoopSleepApplicationTestFixture.ReadyNode()]);
        var harness = new GovernedLoopSleepApplicationHarness(publishedPosture);
        var checkpoint = await harness.PublishAsync();
        var advancedPosture = GovernedLoopSleepApplicationTestFixture.Posture(
            node: waiting,
            frontierVersion: publishedPosture.Execution.Frontier.Payload.FrontierVersion + 1,
            lifecycleStatus: GovernedLoopRunStatus.Running,
            frontierStatus: GovernedLoopFrontierStatus.Active,
            nodes: [waiting, GovernedLoopSleepApplicationTestFixture.RunningNode()]);
        harness.CurrentPosture.Result = new GovernedLoopSleepCurrentPostureReadResult(
            GovernedLoopSleepCurrentPostureReadStatus.Found,
            advancedPosture);

        var result = await harness.Service.WakeAsync(new GovernedLoopWakeRequest(checkpoint.CheckpointId, checkpoint.ContentHash));

        Assert.NotEqual(checkpoint.Binding.FrontierVersion, advancedPosture.Execution.Frontier.Payload.FrontierVersion);
        Assert.NotEqual(checkpoint.Binding.FrontierHash, advancedPosture.Execution.Frontier.Payload.ContentHash);
        Assert.Equal(GovernedLoopWakeResultStatus.Committed, result.Status);
        Assert.Equal(1, harness.Continuation.ContinueCount);
    }

    [Fact]
    public async Task Wake_rejects_changed_waiting_activation_after_sibling_progress()
    {
        var waiting = GovernedLoopSleepApplicationTestFixture.WaitingNode();
        var publishedPosture = GovernedLoopSleepApplicationTestFixture.Posture(
            node: waiting,
            lifecycleStatus: GovernedLoopRunStatus.Running,
            frontierStatus: GovernedLoopFrontierStatus.Active,
            nodes: [waiting, GovernedLoopSleepApplicationTestFixture.ReadyNode()]);
        var harness = new GovernedLoopSleepApplicationHarness(publishedPosture);
        var checkpoint = await harness.PublishAsync();
        var changedWait = GovernedLoopSleepApplicationTestFixture.WaitingNode(
            waitAttempt: waiting.Attempt!.Value + 1,
            waitOperationId: "wait-operation-2");
        var advancedPosture = GovernedLoopSleepApplicationTestFixture.Posture(
            node: changedWait,
            frontierVersion: publishedPosture.Execution.Frontier.Payload.FrontierVersion + 1,
            lifecycleStatus: GovernedLoopRunStatus.Running,
            frontierStatus: GovernedLoopFrontierStatus.Active,
            nodes: [changedWait, GovernedLoopSleepApplicationTestFixture.RunningNode()]);
        harness.CurrentPosture.Result = new GovernedLoopSleepCurrentPostureReadResult(
            GovernedLoopSleepCurrentPostureReadStatus.Found,
            advancedPosture);

        var result = await harness.Service.WakeAsync(new GovernedLoopWakeRequest(checkpoint.CheckpointId, checkpoint.ContentHash));

        Assert.Equal(GovernedLoopWakeResultStatus.Stale, result.Status);
        Assert.Equal(GovernedLoopWakeDisposition.Stale, result.Evidence!.Disposition);
        Assert.Equal(0, harness.Continuation.ContinueCount);
    }

    [Fact]
    public async Task Authenticated_event_requires_proof_and_exact_replay_is_duplicate()
    {
        var harness = new GovernedLoopSleepApplicationHarness(mode: GovernedLoopWakeMode.AuthenticatedEvent);
        var checkpoint = await harness.PublishAsync();
        var invalid = await harness.Service.WakeAsync(new GovernedLoopWakeRequest(checkpoint.CheckpointId, checkpoint.ContentHash));
        var request = new GovernedLoopWakeRequest(
            checkpoint.CheckpointId,
            checkpoint.ContentHash,
            GovernedLoopSleepApplicationTestFixture.Hash('7'));

        var first = await harness.Service.WakeAsync(request);
        var replay = await harness.Service.WakeAsync(request);

        Assert.Equal(GovernedLoopWakeResultStatus.Invalid, invalid.Status);
        Assert.Equal(GovernedLoopWakeResultStatus.Committed, first.Status);
        Assert.Equal(GovernedLoopWakeResultStatus.Duplicate, replay.Status);
        Assert.Equal(first.Evidence!.Identity.WakeId, replay.Evidence!.Identity.WakeId);
        Assert.Equal(1, harness.Continuation.ContinueCount);
        Assert.Equal(2, harness.AuthenticatedWakeVerification.VerifyCount);
        Assert.Equal(checkpoint.CheckpointId, harness.AuthenticatedWakeVerification.LastRequest!.CheckpointId);
        Assert.Equal(checkpoint.AuthenticatedEventReference, harness.AuthenticatedWakeVerification.LastRequest.AuthenticatedEventReference);
        Assert.Equal(request.AuthenticationEvidenceHash, harness.AuthenticatedWakeVerification.LastRequest.AuthenticationEvidenceHash);
    }

    [Fact]
    public async Task Authenticated_event_rejection_or_ineligibility_never_prepares_continuation()
    {
        var rejected = new GovernedLoopSleepApplicationHarness(mode: GovernedLoopWakeMode.AuthenticatedEvent);
        var rejectedCheckpoint = await rejected.PublishAsync();
        rejected.AuthenticatedWakeVerification.Result = new GovernedLoopAuthenticatedWakeVerificationResult(
            GovernedLoopAuthenticatedWakeVerificationStatus.Rejected);

        var ineligible = new GovernedLoopSleepApplicationHarness(mode: GovernedLoopWakeMode.AuthenticatedEvent);
        var ineligibleCheckpoint = await ineligible.PublishAsync();
        var ineligibleVerification = Verified(
            ineligibleCheckpoint,
            GovernedLoopSleepApplicationTestFixture.Hash('7'));
        ineligible.AuthenticatedWakeVerification.Result = ineligibleVerification with
        {
            Verification = ineligibleVerification.Verification! with { Eligible = false }
        };

        var rejectedResult = await rejected.Service.WakeAsync(new GovernedLoopWakeRequest(
            rejectedCheckpoint.CheckpointId,
            rejectedCheckpoint.ContentHash,
            GovernedLoopSleepApplicationTestFixture.Hash('7')));
        var ineligibleResult = await ineligible.Service.WakeAsync(new GovernedLoopWakeRequest(
            ineligibleCheckpoint.CheckpointId,
            ineligibleCheckpoint.ContentHash,
            GovernedLoopSleepApplicationTestFixture.Hash('7')));

        Assert.Equal(GovernedLoopWakeResultStatus.Invalid, rejectedResult.Status);
        Assert.Equal(GovernedLoopWakeResultStatus.NotEligible, ineligibleResult.Status);
        Assert.Equal(0, rejected.Store.WakeCount);
        Assert.Equal(0, ineligible.Store.WakeCount);
        Assert.Equal(0, rejected.Continuation.ContinueCount);
        Assert.Equal(0, ineligible.Continuation.ContinueCount);
    }

    [Theory]
    [InlineData(GovernedLoopAuthenticatedWakeVerificationStatus.NotFound, GovernedLoopWakeResultStatus.NotFound)]
    [InlineData(GovernedLoopAuthenticatedWakeVerificationStatus.Conflict, GovernedLoopWakeResultStatus.Conflict)]
    [InlineData(GovernedLoopAuthenticatedWakeVerificationStatus.Unavailable, GovernedLoopWakeResultStatus.Unavailable)]
    public async Task Authenticated_event_maps_authoritative_verification_failures(
        GovernedLoopAuthenticatedWakeVerificationStatus verificationStatus,
        GovernedLoopWakeResultStatus expected)
    {
        var harness = new GovernedLoopSleepApplicationHarness(mode: GovernedLoopWakeMode.AuthenticatedEvent);
        var checkpoint = await harness.PublishAsync();
        harness.AuthenticatedWakeVerification.Result = new GovernedLoopAuthenticatedWakeVerificationResult(verificationStatus);

        var result = await harness.Service.WakeAsync(new GovernedLoopWakeRequest(
            checkpoint.CheckpointId,
            checkpoint.ContentHash,
            GovernedLoopSleepApplicationTestFixture.Hash('7')));

        Assert.Equal(expected, result.Status);
        Assert.Equal(0, harness.Store.WakeCount);
        Assert.Equal(0, harness.Continuation.ContinueCount);
    }

    [Fact]
    public async Task Authenticated_event_rejects_forged_reference_hash_and_chronology()
    {
        var mutations = new Func<GovernedLoopAuthenticatedWakeVerification, GovernedLoopAuthenticatedWakeVerification>[]
        {
            verification => verification with { CheckpointId = GovernedLoopSleepApplicationTestFixture.Hash('2') },
            verification => verification with { CheckpointHash = GovernedLoopSleepApplicationTestFixture.Hash('3') },
            verification => verification with { AuthenticatedEventReference = "forged-event-reference" },
            verification => verification with { AuthenticationEvidenceHash = GovernedLoopSleepApplicationTestFixture.Hash('8') },
            verification => verification with { OccurredAtUtc = GovernedLoopSleepApplicationTestFixture.Now.AddSeconds(-1) },
            verification => verification with { AuthenticatedAtUtc = GovernedLoopSleepApplicationTestFixture.Now.AddSeconds(1) },
            verification => verification with
            {
                OccurredAtUtc = GovernedLoopSleepApplicationTestFixture.Now.ToOffset(TimeSpan.FromHours(1)),
                AuthenticatedAtUtc = GovernedLoopSleepApplicationTestFixture.Now.ToOffset(TimeSpan.FromHours(1))
            }
        };

        foreach (var mutate in mutations)
        {
            var harness = new GovernedLoopSleepApplicationHarness(mode: GovernedLoopWakeMode.AuthenticatedEvent);
            var checkpoint = await harness.PublishAsync();
            var verified = Verified(checkpoint, GovernedLoopSleepApplicationTestFixture.Hash('7'));
            harness.AuthenticatedWakeVerification.Result = verified with
            {
                Verification = mutate(verified.Verification!)
            };

            var result = await harness.Service.WakeAsync(new GovernedLoopWakeRequest(
                checkpoint.CheckpointId,
                checkpoint.ContentHash,
                GovernedLoopSleepApplicationTestFixture.Hash('7')));

            Assert.Equal(GovernedLoopWakeResultStatus.Invalid, result.Status);
            Assert.Equal(0, harness.Store.WakeCount);
            Assert.Equal(0, harness.Continuation.ContinueCount);
        }
    }

    [Fact]
    public async Task Authenticated_event_fails_closed_for_throwing_null_malformed_or_clock_failed_verification()
    {
        var throwing = new GovernedLoopSleepApplicationHarness(mode: GovernedLoopWakeMode.AuthenticatedEvent);
        var throwingCheckpoint = await throwing.PublishAsync();
        throwing.AuthenticatedWakeVerification.Exception = new InvalidOperationException("verification unavailable");

        var nullOutput = new GovernedLoopSleepApplicationHarness(mode: GovernedLoopWakeMode.AuthenticatedEvent);
        var nullCheckpoint = await nullOutput.PublishAsync();
        nullOutput.AuthenticatedWakeVerification.ReturnNull = true;

        var malformed = new GovernedLoopSleepApplicationHarness(mode: GovernedLoopWakeMode.AuthenticatedEvent);
        var malformedCheckpoint = await malformed.PublishAsync();
        malformed.AuthenticatedWakeVerification.Result = new GovernedLoopAuthenticatedWakeVerificationResult(
            GovernedLoopAuthenticatedWakeVerificationStatus.Verified);

        var malformedRejected = new GovernedLoopSleepApplicationHarness(mode: GovernedLoopWakeMode.AuthenticatedEvent);
        var malformedRejectedCheckpoint = await malformedRejected.PublishAsync();
        var rejectedWithEvidence = Verified(
            malformedRejectedCheckpoint,
            GovernedLoopSleepApplicationTestFixture.Hash('7'));
        malformedRejected.AuthenticatedWakeVerification.Result = rejectedWithEvidence with
        {
            Status = GovernedLoopAuthenticatedWakeVerificationStatus.Rejected
        };

        var unknown = new GovernedLoopSleepApplicationHarness(mode: GovernedLoopWakeMode.AuthenticatedEvent);
        var unknownCheckpoint = await unknown.PublishAsync();
        unknown.AuthenticatedWakeVerification.Result = new GovernedLoopAuthenticatedWakeVerificationResult(
            (GovernedLoopAuthenticatedWakeVerificationStatus)int.MaxValue);

        var firstClockFailure = new GovernedLoopSleepApplicationHarness(mode: GovernedLoopWakeMode.AuthenticatedEvent);
        var firstClockCheckpoint = await firstClockFailure.PublishAsync();
        firstClockFailure.TimeProvider.ThrowOnCall = firstClockFailure.TimeProvider.CallCount + 1;

        var clockFailure = new GovernedLoopSleepApplicationHarness(mode: GovernedLoopWakeMode.AuthenticatedEvent);
        var clockCheckpoint = await clockFailure.PublishAsync();
        clockFailure.TimeProvider.ThrowOnCall = clockFailure.TimeProvider.CallCount + 2;

        var hash = GovernedLoopSleepApplicationTestFixture.Hash('7');
        Assert.Equal(
            GovernedLoopWakeResultStatus.Unavailable,
            (await throwing.Service.WakeAsync(new GovernedLoopWakeRequest(
                throwingCheckpoint.CheckpointId,
                throwingCheckpoint.ContentHash,
                hash))).Status);
        Assert.Equal(
            GovernedLoopWakeResultStatus.Invalid,
            (await nullOutput.Service.WakeAsync(new GovernedLoopWakeRequest(
                nullCheckpoint.CheckpointId,
                nullCheckpoint.ContentHash,
                hash))).Status);
        Assert.Equal(
            GovernedLoopWakeResultStatus.Invalid,
            (await malformed.Service.WakeAsync(new GovernedLoopWakeRequest(
                malformedCheckpoint.CheckpointId,
                malformedCheckpoint.ContentHash,
                hash))).Status);
        Assert.Equal(
            GovernedLoopWakeResultStatus.Invalid,
            (await malformedRejected.Service.WakeAsync(new GovernedLoopWakeRequest(
                malformedRejectedCheckpoint.CheckpointId,
                malformedRejectedCheckpoint.ContentHash,
                hash))).Status);
        Assert.Equal(
            GovernedLoopWakeResultStatus.Invalid,
            (await unknown.Service.WakeAsync(new GovernedLoopWakeRequest(
                unknownCheckpoint.CheckpointId,
                unknownCheckpoint.ContentHash,
                hash))).Status);
        Assert.Equal(
            GovernedLoopWakeResultStatus.Unavailable,
            (await firstClockFailure.Service.WakeAsync(new GovernedLoopWakeRequest(
                firstClockCheckpoint.CheckpointId,
                firstClockCheckpoint.ContentHash,
                hash))).Status);
        Assert.Equal(
            GovernedLoopWakeResultStatus.Unavailable,
            (await clockFailure.Service.WakeAsync(new GovernedLoopWakeRequest(
                clockCheckpoint.CheckpointId,
                clockCheckpoint.ContentHash,
                hash))).Status);
    }

    [Fact]
    public async Task Different_authenticated_event_after_claim_is_late_not_a_second_continuation()
    {
        var harness = new GovernedLoopSleepApplicationHarness(mode: GovernedLoopWakeMode.AuthenticatedEvent);
        var checkpoint = await harness.PublishAsync();
        var first = await harness.Service.WakeAsync(new GovernedLoopWakeRequest(
            checkpoint.CheckpointId,
            checkpoint.ContentHash,
            GovernedLoopSleepApplicationTestFixture.Hash('7')));

        var late = await harness.Service.WakeAsync(new GovernedLoopWakeRequest(
            checkpoint.CheckpointId,
            checkpoint.ContentHash,
            GovernedLoopSleepApplicationTestFixture.Hash('8')));

        Assert.Equal(GovernedLoopWakeResultStatus.Committed, first.Status);
        Assert.Equal(GovernedLoopWakeResultStatus.Late, late.Status);
        Assert.Equal(1, harness.Store.WakeCount);
        Assert.Equal(1, harness.Continuation.CommittedOperationCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Wake_persists_stale_for_replaced_generation_visit_or_cycle(int substitution)
    {
        var harness = new GovernedLoopSleepApplicationHarness();
        var checkpoint = await harness.PublishAsync();
        var current = substitution switch
        {
            0 => GovernedLoopSleepApplicationTestFixture.Posture(binding: GovernedLoopSleepApplicationTestFixture.Binding(2)),
            1 => GovernedLoopSleepApplicationTestFixture.Posture(
                node: GovernedLoopSleepApplicationTestFixture.WaitingNode(
                    activationOrdinal: 1,
                    visitOrdinal: 2,
                    cycleId: "cycle-visit",
                    cycleIteration: 2),
                frontierVersion: 8,
                nodes:
                [
                    GovernedLoopSleepApplicationTestFixture.CompletedWaitNode(),
                    GovernedLoopSleepApplicationTestFixture.WaitingNode(
                        activationOrdinal: 1,
                        visitOrdinal: 2,
                        cycleId: "cycle-visit",
                        cycleIteration: 2)
                ]),
            _ => GovernedLoopSleepApplicationTestFixture.Posture(
                node: GovernedLoopSleepApplicationTestFixture.WaitingNode(cycleId: "cycle-2", cycleIteration: 2))
        };
        harness.CurrentPosture.Result = new GovernedLoopSleepCurrentPostureReadResult(
            GovernedLoopSleepCurrentPostureReadStatus.Found,
            current);

        var result = await harness.Service.WakeAsync(new GovernedLoopWakeRequest(checkpoint.CheckpointId, checkpoint.ContentHash));

        Assert.Equal(GovernedLoopWakeResultStatus.Stale, result.Status);
        Assert.Equal(GovernedLoopWakeDisposition.Stale, result.Evidence!.Disposition);
        Assert.Equal(0, harness.Continuation.ContinueCount);
    }

    [Theory]
    [InlineData(GovernedLoopRunStatus.CancelRequested, true, false, GovernedLoopWakeResultStatus.Cancelled)]
    [InlineData(GovernedLoopRunStatus.Cancelled, true, false, GovernedLoopWakeResultStatus.Cancelled)]
    [InlineData(GovernedLoopRunStatus.Paused, true, false, GovernedLoopWakeResultStatus.Paused)]
    [InlineData(GovernedLoopRunStatus.Waiting, false, false, GovernedLoopWakeResultStatus.ReviewBlocked)]
    [InlineData(GovernedLoopRunStatus.Waiting, true, true, GovernedLoopWakeResultStatus.Expired)]
    public async Task Wake_classifies_cancelled_paused_review_and_expired_posture(
        GovernedLoopRunStatus lifecycle,
        bool unattended,
        bool expired,
        GovernedLoopWakeResultStatus expected)
    {
        var harness = new GovernedLoopSleepApplicationHarness();
        var checkpoint = await harness.PublishAsync();
        var current = GovernedLoopSleepApplicationTestFixture.Posture(
            lifecycleStatus: lifecycle,
            unattended: unattended,
            expiresAtUtc: expired ? GovernedLoopSleepApplicationTestFixture.Now : null);
        harness.CurrentPosture.Result = new GovernedLoopSleepCurrentPostureReadResult(
            GovernedLoopSleepCurrentPostureReadStatus.Found,
            current);

        var result = await harness.Service.WakeAsync(new GovernedLoopWakeRequest(checkpoint.CheckpointId, checkpoint.ContentHash));

        Assert.Equal(expected, result.Status);
        Assert.Equal(0, harness.Continuation.ContinueCount);
    }

    [Fact]
    public async Task Wake_preserves_explicit_needs_review_over_changed_review_frontier()
    {
        var harness = new GovernedLoopSleepApplicationHarness();
        var checkpoint = await harness.PublishAsync();
        var binding = GovernedLoopSleepApplicationTestFixture.Binding();
        var review = GovernedLoopSleepApplicationTestFixture.Posture(
            binding,
            lifecycleStatus: GovernedLoopRunStatus.NeedsReview,
            effects: [GovernedLoopSleepApplicationTestFixture.OpenEffect(binding)]);
        harness.CurrentPosture.Result = new GovernedLoopSleepCurrentPostureReadResult(
            GovernedLoopSleepCurrentPostureReadStatus.Found,
            review);

        var result = await harness.Service.WakeAsync(new GovernedLoopWakeRequest(checkpoint.CheckpointId, checkpoint.ContentHash));

        Assert.Equal(GovernedLoopWakeResultStatus.ReviewBlocked, result.Status);
        Assert.Null(result.Evidence);
        Assert.Equal(0, harness.Store.WakeCount);
    }

    [Fact]
    public async Task Wake_blocks_open_effect_as_ambiguous_without_minting_continuation_intent()
    {
        var harness = new GovernedLoopSleepApplicationHarness();
        var checkpoint = await harness.PublishAsync();
        var binding = GovernedLoopSleepApplicationTestFixture.Binding();
        var current = GovernedLoopSleepApplicationTestFixture.Posture(
            binding,
            effects: [GovernedLoopSleepApplicationTestFixture.OpenEffect(binding)]);
        harness.CurrentPosture.Result = new GovernedLoopSleepCurrentPostureReadResult(
            GovernedLoopSleepCurrentPostureReadStatus.Found,
            current);

        var result = await harness.Service.WakeAsync(new GovernedLoopWakeRequest(checkpoint.CheckpointId, checkpoint.ContentHash));

        Assert.Equal(GovernedLoopWakeResultStatus.AmbiguousAttempt, result.Status);
        Assert.Null(result.Evidence);
        Assert.Equal(0, harness.Store.WakeCount);
        Assert.Equal(0, harness.Continuation.ContinueCount);
    }

    [Fact]
    public async Task Paused_or_review_blocked_checkpoint_remains_pending_then_continues_once_after_clearance()
    {
        var paused = new GovernedLoopSleepApplicationHarness();
        var pausedCheckpoint = await paused.PublishAsync();
        paused.CurrentPosture.Result = new GovernedLoopSleepCurrentPostureReadResult(
            GovernedLoopSleepCurrentPostureReadStatus.Found,
            GovernedLoopSleepApplicationTestFixture.Posture(lifecycleStatus: GovernedLoopRunStatus.Paused));
        var pausedResult = await paused.Service.WakeAsync(new GovernedLoopWakeRequest(
            pausedCheckpoint.CheckpointId,
            pausedCheckpoint.ContentHash));
        paused.CurrentPosture.Result = new GovernedLoopSleepCurrentPostureReadResult(
            GovernedLoopSleepCurrentPostureReadStatus.Found,
            paused.Posture);
        var resumed = await paused.Service.WakeAsync(new GovernedLoopWakeRequest(
            pausedCheckpoint.CheckpointId,
            pausedCheckpoint.ContentHash));

        var review = new GovernedLoopSleepApplicationHarness();
        var reviewCheckpoint = await review.PublishAsync();
        var reviewBinding = GovernedLoopSleepApplicationTestFixture.Binding();
        review.CurrentPosture.Result = new GovernedLoopSleepCurrentPostureReadResult(
            GovernedLoopSleepCurrentPostureReadStatus.Found,
            GovernedLoopSleepApplicationTestFixture.Posture(
                reviewBinding,
                lifecycleStatus: GovernedLoopRunStatus.NeedsReview,
                effects: [GovernedLoopSleepApplicationTestFixture.OpenEffect(reviewBinding)]));
        var blocked = await review.Service.WakeAsync(new GovernedLoopWakeRequest(
            reviewCheckpoint.CheckpointId,
            reviewCheckpoint.ContentHash));
        review.CurrentPosture.Result = new GovernedLoopSleepCurrentPostureReadResult(
            GovernedLoopSleepCurrentPostureReadStatus.Found,
            review.Posture);
        var approved = await review.Service.WakeAsync(new GovernedLoopWakeRequest(
            reviewCheckpoint.CheckpointId,
            reviewCheckpoint.ContentHash));

        Assert.Equal(GovernedLoopWakeResultStatus.Paused, pausedResult.Status);
        Assert.Null(pausedResult.Evidence);
        Assert.Equal(GovernedLoopWakeResultStatus.Committed, resumed.Status);
        Assert.Equal(1, paused.Store.WakeCount);
        Assert.Equal(1, paused.Continuation.ContinueCount);
        Assert.Equal(GovernedLoopWakeResultStatus.ReviewBlocked, blocked.Status);
        Assert.Null(blocked.Evidence);
        Assert.Equal(GovernedLoopWakeResultStatus.Committed, approved.Status);
        Assert.Equal(1, review.Store.WakeCount);
        Assert.Equal(1, review.Continuation.ContinueCount);
    }

    [Theory]
    [InlineData(GovernedLoopWakeEvidenceMutationStatus.Conflict, GovernedLoopWakeResultStatus.Conflict)]
    [InlineData(GovernedLoopWakeEvidenceMutationStatus.Unavailable, GovernedLoopWakeResultStatus.Unavailable)]
    [InlineData(GovernedLoopWakeEvidenceMutationStatus.Ambiguous, GovernedLoopWakeResultStatus.AmbiguousAttempt)]
    public async Task Wake_maps_optimistic_store_outcomes_without_invoking_continuation(
        GovernedLoopWakeEvidenceMutationStatus storeStatus,
        GovernedLoopWakeResultStatus expected)
    {
        var harness = new GovernedLoopSleepApplicationHarness();
        var checkpoint = await harness.PublishAsync();
        harness.Store.CreateOverride = new GovernedLoopWakeEvidenceMutationResult(storeStatus);

        var result = await harness.Service.WakeAsync(new GovernedLoopWakeRequest(checkpoint.CheckpointId, checkpoint.ContentHash));

        Assert.Equal(expected, result.Status);
        Assert.Equal(0, harness.Continuation.ContinueCount);
    }

    [Theory]
    [InlineData(GovernedLoopSleepCurrentPostureReadStatus.NotFound, GovernedLoopWakeResultStatus.NotFound)]
    [InlineData(GovernedLoopSleepCurrentPostureReadStatus.Conflict, GovernedLoopWakeResultStatus.Conflict)]
    [InlineData(GovernedLoopSleepCurrentPostureReadStatus.Unavailable, GovernedLoopWakeResultStatus.Unavailable)]
    public async Task Wake_maps_current_posture_read_failures_without_claiming_checkpoint(
        GovernedLoopSleepCurrentPostureReadStatus readStatus,
        GovernedLoopWakeResultStatus expected)
    {
        var harness = new GovernedLoopSleepApplicationHarness();
        var checkpoint = await harness.PublishAsync();
        harness.CurrentPosture.Result = new GovernedLoopSleepCurrentPostureReadResult(readStatus);

        var result = await harness.Service.WakeAsync(new GovernedLoopWakeRequest(checkpoint.CheckpointId, checkpoint.ContentHash));

        Assert.Equal(expected, result.Status);
        Assert.Equal(0, harness.Store.WakeCount);
        Assert.Equal(0, harness.Continuation.ContinueCount);
    }

    [Fact]
    public async Task Wake_fails_closed_when_current_posture_or_clock_is_unavailable()
    {
        var postureFailure = new GovernedLoopSleepApplicationHarness();
        var postureCheckpoint = await postureFailure.PublishAsync();
        postureFailure.CurrentPosture.Exception = new InvalidOperationException("posture unavailable");

        var firstClockFailure = new GovernedLoopSleepApplicationHarness();
        var firstClockCheckpoint = await firstClockFailure.PublishAsync();
        firstClockFailure.TimeProvider.ThrowOnCall = firstClockFailure.TimeProvider.CallCount + 1;

        var secondClockFailure = new GovernedLoopSleepApplicationHarness();
        var secondClockCheckpoint = await secondClockFailure.PublishAsync();
        secondClockFailure.TimeProvider.ThrowOnCall = secondClockFailure.TimeProvider.CallCount + 2;

        Assert.Equal(
            GovernedLoopWakeResultStatus.Unavailable,
            (await postureFailure.Service.WakeAsync(new GovernedLoopWakeRequest(
                postureCheckpoint.CheckpointId,
                postureCheckpoint.ContentHash))).Status);
        Assert.Equal(
            GovernedLoopWakeResultStatus.Unavailable,
            (await firstClockFailure.Service.WakeAsync(new GovernedLoopWakeRequest(
                firstClockCheckpoint.CheckpointId,
                firstClockCheckpoint.ContentHash))).Status);
        Assert.Equal(
            GovernedLoopWakeResultStatus.Unavailable,
            (await secondClockFailure.Service.WakeAsync(new GovernedLoopWakeRequest(
                secondClockCheckpoint.CheckpointId,
                secondClockCheckpoint.ContentHash))).Status);
    }

    [Theory]
    [InlineData(GovernedLoopWakeDisposition.Committed, GovernedLoopWakeResultStatus.Duplicate)]
    [InlineData(GovernedLoopWakeDisposition.Duplicate, GovernedLoopWakeResultStatus.Duplicate)]
    [InlineData(GovernedLoopWakeDisposition.Late, GovernedLoopWakeResultStatus.Late)]
    [InlineData(GovernedLoopWakeDisposition.Conflict, GovernedLoopWakeResultStatus.Conflict)]
    public async Task Wake_projects_existing_terminal_evidence_without_redispatch(
        GovernedLoopWakeDisposition disposition,
        GovernedLoopWakeResultStatus expected)
    {
        var harness = new GovernedLoopSleepApplicationHarness();
        var checkpoint = await harness.PublishAsync();
        var evidence = GovernedLoopSleepApplicationTestFixture.Terminal(checkpoint, disposition);
        harness.Store.SeedWake(evidence);

        var result = await harness.Service.WakeAsync(new GovernedLoopWakeRequest(checkpoint.CheckpointId, checkpoint.ContentHash));

        Assert.Equal(expected, result.Status);
        Assert.Same(evidence, result.Evidence);
        Assert.Equal(0, harness.Continuation.ContinueCount);
    }

    [Fact]
    public async Task Wake_maps_store_read_exceptions_to_unavailable()
    {
        var checkpointRead = new GovernedLoopSleepApplicationHarness();
        var checkpoint = await checkpointRead.PublishAsync();
        checkpointRead.Store.CheckpointReadException = new InvalidOperationException("checkpoint read unavailable");

        var wakeRead = new GovernedLoopSleepApplicationHarness();
        var wakeCheckpoint = await wakeRead.PublishAsync();
        wakeRead.Store.WakeReadException = new InvalidOperationException("wake read unavailable");

        Assert.Equal(
            GovernedLoopWakeResultStatus.Unavailable,
            (await checkpointRead.Service.WakeAsync(new GovernedLoopWakeRequest(
                checkpoint.CheckpointId,
                checkpoint.ContentHash))).Status);
        Assert.Equal(
            GovernedLoopWakeResultStatus.Unavailable,
            (await wakeRead.Service.WakeAsync(new GovernedLoopWakeRequest(
                wakeCheckpoint.CheckpointId,
                wakeCheckpoint.ContentHash))).Status);
    }

    [Fact]
    public async Task Wake_rejects_wrong_hash_missing_checkpoint_and_malformed_store_artifact()
    {
        var harness = new GovernedLoopSleepApplicationHarness();
        var checkpoint = await harness.PublishAsync();
        Assert.Equal(GovernedLoopWakeResultStatus.Invalid, (await harness.Service.WakeAsync(null)).Status);
        Assert.Equal(
            GovernedLoopWakeResultStatus.Invalid,
            (await harness.Service.WakeAsync(new GovernedLoopWakeRequest(checkpoint.CheckpointId, GovernedLoopSleepApplicationTestFixture.Hash('1')))).Status);
        Assert.Equal(
            GovernedLoopWakeResultStatus.Invalid,
            (await harness.Service.WakeAsync(new GovernedLoopWakeRequest(
                checkpoint.CheckpointId,
                checkpoint.ContentHash,
                GovernedLoopSleepApplicationTestFixture.Hash('7')))).Status);
        Assert.Equal(
            GovernedLoopWakeResultStatus.NotFound,
            (await harness.Service.WakeAsync(new GovernedLoopWakeRequest(GovernedLoopSleepApplicationTestFixture.Hash('2'), GovernedLoopSleepApplicationTestFixture.Hash('3')))).Status);

        harness.Store.CheckpointReadOverride = new GovernedLoopSleepCheckpointReadResult(
            GovernedLoopSleepStoreReadStatus.Found);
        Assert.Equal(
            GovernedLoopWakeResultStatus.Conflict,
            (await harness.Service.WakeAsync(new GovernedLoopWakeRequest(checkpoint.CheckpointId, checkpoint.ContentHash))).Status);
    }

    [Fact]
    public async Task Wake_fails_closed_for_null_store_read_create_and_advance_outputs()
    {
        var checkpointRead = new GovernedLoopSleepApplicationHarness();
        var checkpoint = await checkpointRead.PublishAsync();
        checkpointRead.Store.ReturnNullCheckpointRead = true;
        Assert.Equal(
            GovernedLoopWakeResultStatus.Conflict,
            (await checkpointRead.Service.WakeAsync(new GovernedLoopWakeRequest(checkpoint.CheckpointId, checkpoint.ContentHash))).Status);

        var wakeRead = new GovernedLoopSleepApplicationHarness();
        checkpoint = await wakeRead.PublishAsync();
        wakeRead.Store.ReturnNullWakeRead = true;
        Assert.Equal(
            GovernedLoopWakeResultStatus.Conflict,
            (await wakeRead.Service.WakeAsync(new GovernedLoopWakeRequest(checkpoint.CheckpointId, checkpoint.ContentHash))).Status);

        var create = new GovernedLoopSleepApplicationHarness();
        checkpoint = await create.PublishAsync();
        create.Store.ReturnNullCreate = true;
        Assert.Equal(
            GovernedLoopWakeResultStatus.Invalid,
            (await create.Service.WakeAsync(new GovernedLoopWakeRequest(checkpoint.CheckpointId, checkpoint.ContentHash))).Status);

        var advance = new GovernedLoopSleepApplicationHarness();
        checkpoint = await advance.PublishAsync();
        advance.Store.ReturnNullAdvance = true;
        Assert.Equal(
            GovernedLoopWakeResultStatus.AmbiguousAttempt,
            (await advance.Service.WakeAsync(new GovernedLoopWakeRequest(checkpoint.CheckpointId, checkpoint.ContentHash))).Status);
    }

    [Theory]
    [InlineData(GovernedLoopSleepStoreReadStatus.Conflict, GovernedLoopWakeResultStatus.Conflict)]
    [InlineData(GovernedLoopSleepStoreReadStatus.Unavailable, GovernedLoopWakeResultStatus.Unavailable)]
    public async Task Wake_reconciles_ambiguous_initial_write_with_authoritative_read_status(
        GovernedLoopSleepStoreReadStatus readStatus,
        GovernedLoopWakeResultStatus expected)
    {
        var harness = new GovernedLoopSleepApplicationHarness();
        var checkpoint = await harness.PublishAsync();
        harness.Store.CreateOverride = new GovernedLoopWakeEvidenceMutationResult(
            GovernedLoopWakeEvidenceMutationStatus.Ambiguous);
        harness.Store.WakeReadOverride = new GovernedLoopWakeEvidenceReadResult(readStatus);

        var result = await harness.Service.WakeAsync(new GovernedLoopWakeRequest(checkpoint.CheckpointId, checkpoint.ContentHash));

        Assert.Equal(expected, result.Status);
        Assert.Equal(0, harness.Continuation.ContinueCount);
    }

    [Fact]
    public async Task Wake_projects_replayed_terminal_evidence_without_continuation()
    {
        var harness = new GovernedLoopSleepApplicationHarness();
        var checkpoint = await harness.PublishAsync();
        var stale = GovernedLoopSleepApplicationTestFixture.Terminal(
            checkpoint,
            GovernedLoopWakeDisposition.Stale);
        harness.Store.CreateOverride = new GovernedLoopWakeEvidenceMutationResult(
            GovernedLoopWakeEvidenceMutationStatus.Replayed,
            stale);

        var result = await harness.Service.WakeAsync(new GovernedLoopWakeRequest(checkpoint.CheckpointId, checkpoint.ContentHash));

        Assert.Equal(GovernedLoopWakeResultStatus.Stale, result.Status);
        Assert.Same(stale, result.Evidence);
        Assert.Equal(0, harness.Continuation.ContinueCount);
    }

    private static GovernedLoopAuthenticatedWakeVerificationResult Verified(
        GovernedLoopSleepCheckpoint checkpoint,
        string authenticationEvidenceHash)
        => new(
            GovernedLoopAuthenticatedWakeVerificationStatus.Verified,
            new GovernedLoopAuthenticatedWakeVerification(
                checkpoint.CheckpointId,
                checkpoint.ContentHash,
                checkpoint.AuthenticatedEventReference!,
                authenticationEvidenceHash,
                checkpoint.PublishedAtUtc,
                GovernedLoopSleepApplicationTestFixture.Now,
                Eligible: true));
}
