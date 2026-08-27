using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Startup.Loops.Execution.Sleep;
using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

public sealed class GovernedLoopLocalCoordinatorTests
{
    private static readonly DateTimeOffset _now = new(2026, 8, 11, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Start_and_stop_are_idempotent_and_publish_heartbeat_and_lifecycle_evidence()
    {
        var evidence = new RecordingCoordinatorEvidencePort();
        var work = new ScriptedLocalWorkRunner();
        var clock = Clock();
        await using var coordinator = Coordinator(evidence, work, clock, "owner-a", heartbeat: TimeSpan.FromMilliseconds(10));

        var started = await coordinator.StartAsync();
        var duplicate = await coordinator.StartAsync();
        await WaitUntilAsync(() => evidence.Heartbeats.Count >= 2);
        var stopped = await coordinator.StopAsync();
        var duplicateStop = await coordinator.StopAsync();

        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Started, started.Status);
        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.AlreadyRunning, duplicate.Status);
        Assert.Equal(GovernedLoopLocalCoordinatorStopStatus.Stopped, stopped.Status);
        Assert.Equal(GovernedLoopLocalCoordinatorStopStatus.AlreadyStopped, duplicateStop.Status);
        Assert.Equal(
            [
                GovernedLoopCoordinatorStatus.Starting,
                GovernedLoopCoordinatorStatus.Running,
                GovernedLoopCoordinatorStatus.Stopping,
                GovernedLoopCoordinatorStatus.Stopped
            ],
            evidence.Lifecycles.Select(item => item.Status));
        Assert.True(evidence.Heartbeats[^1].HeartbeatSequence > 1);
    }

    [Fact]
    public async Task Runs_without_browser_or_request_lifetime_and_rotates_bounded_family_fairness()
    {
        var evidence = new RecordingCoordinatorEvidencePort();
        var work = new ScriptedLocalWorkRunner
        {
            Handler = static (_, _) => Task.FromResult<GovernedLoopLocalWorkResult?>(
                new GovernedLoopLocalWorkResult(GovernedLoopLocalWorkResultStatus.Completed, "work-completed"))
        };
        await using var coordinator = Coordinator(
            evidence,
            work,
            Clock(),
            "owner-a",
            cycle: TimeSpan.FromMilliseconds(5),
            perFamily: 2);

        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Started, (await coordinator.StartAsync()).Status);
        await WaitUntilAsync(() => work.CallCount >= 12);
        Assert.Equal(GovernedLoopLocalCoordinatorStopStatus.Stopped, (await coordinator.StopAsync()).Status);

        Assert.Equal(
            [
                GovernedLoopLocalWorkFamily.Schedule,
                GovernedLoopLocalWorkFamily.Schedule,
                GovernedLoopLocalWorkFamily.Trigger,
                GovernedLoopLocalWorkFamily.Trigger,
                GovernedLoopLocalWorkFamily.Wake,
                GovernedLoopLocalWorkFamily.Wake,
                GovernedLoopLocalWorkFamily.Trigger,
                GovernedLoopLocalWorkFamily.Trigger,
                GovernedLoopLocalWorkFamily.Wake,
                GovernedLoopLocalWorkFamily.Wake,
                GovernedLoopLocalWorkFamily.Schedule,
                GovernedLoopLocalWorkFamily.Schedule
            ],
            work.Calls.Take(12));
    }

    [Fact]
    public async Task Two_concurrent_owners_never_both_acquire_the_same_live_coordinator()
    {
        var evidence = new RecordingCoordinatorEvidencePort();
        var clock = Clock();
        await using var first = Coordinator(evidence, new ScriptedLocalWorkRunner(), clock, "owner-a");
        await using var second = Coordinator(evidence, new ScriptedLocalWorkRunner(), clock, "owner-b");

        var results = await Task.WhenAll(first.StartAsync(), second.StartAsync());

        Assert.Single(results, item => item.Status == GovernedLoopLocalCoordinatorStartStatus.Started);
        Assert.Single(results, item => item.Status is GovernedLoopLocalCoordinatorStartStatus.OwnedByLivePeer
            or GovernedLoopLocalCoordinatorStartStatus.Conflict);

        _ = await first.StopAsync();
        _ = await second.StopAsync();
    }

    [Fact]
    public async Task Expired_owner_handoff_rehydrates_every_durable_work_family_after_restart()
    {
        var evidence = new RecordingCoordinatorEvidencePort();
        var clock = Clock();
        await using var first = Coordinator(
            evidence,
            new ScriptedLocalWorkRunner(),
            clock,
            "owner-a",
            heartbeat: TimeSpan.FromMilliseconds(500),
            lease: TimeSpan.FromSeconds(1));
        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Started, (await first.StartAsync()).Status);
        Assert.Equal(GovernedLoopLocalCoordinatorStopStatus.Stopped, (await first.StopAsync()).Status);

        clock.Advance(TimeSpan.FromSeconds(2));
        var rehydrated = new ScriptedLocalWorkRunner();
        await using var second = Coordinator(
            evidence,
            rehydrated,
            clock,
            "owner-b",
            heartbeat: TimeSpan.FromMilliseconds(500),
            lease: TimeSpan.FromSeconds(1));
        var restarted = await second.StartAsync();
        await WaitUntilAsync(() => rehydrated.CallCount >= 3);
        await second.StopAsync();

        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Started, restarted.Status);
        Assert.Equal(2, restarted.Snapshot!.Ownership.OwnershipEpoch);
        Assert.Contains(GovernedLoopLocalWorkFamily.Schedule, rehydrated.Calls);
        Assert.Contains(GovernedLoopLocalWorkFamily.Trigger, rehydrated.Calls);
        Assert.Contains(GovernedLoopLocalWorkFamily.Wake, rehydrated.Calls);
    }

    [Fact]
    public async Task Expired_owner_cannot_admit_more_work_after_a_successor_takes_over()
    {
        var firstWorkEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var evidence = new RecordingCoordinatorEvidencePort();
        var clock = Clock();
        var firstWork = new ScriptedLocalWorkRunner
        {
            Handler = async (_, _) =>
            {
                firstWorkEntered.TrySetResult();
                await releaseFirstWork.Task;
                return new GovernedLoopLocalWorkResult(GovernedLoopLocalWorkResultStatus.Completed, "safe-boundary");
            }
        };
        await using var first = Coordinator(
            evidence,
            firstWork,
            clock,
            "owner-a",
            cycle: TimeSpan.FromMilliseconds(1),
            heartbeat: TimeSpan.FromSeconds(5),
            lease: TimeSpan.FromSeconds(10));
        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Started, (await first.StartAsync()).Status);
        await firstWorkEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        clock.Advance(TimeSpan.FromSeconds(11));
        var successorWork = new ScriptedLocalWorkRunner();
        await using var successor = Coordinator(
            evidence,
            successorWork,
            clock,
            "owner-b",
            cycle: TimeSpan.FromMilliseconds(1),
            heartbeat: TimeSpan.FromSeconds(5),
            lease: TimeSpan.FromSeconds(10));
        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Started, (await successor.StartAsync()).Status);
        await WaitUntilAsync(() => successorWork.CallCount > 0);

        releaseFirstWork.TrySetResult();
        var firstStopped = await first.StopAsync();
        var successorStopped = await successor.StopAsync();
        var firstRestart = await first.StartAsync();

        Assert.Equal(GovernedLoopLocalCoordinatorStopStatus.OwnershipLost, firstStopped.Status);
        Assert.Equal(GovernedLoopLocalCoordinatorStopStatus.Stopped, successorStopped.Status);
        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.OwnedByLivePeer, firstRestart.Status);
        Assert.Equal(1, firstWork.CallCount);
        Assert.Equal(2, evidence.Snapshot!.Ownership.OwnershipEpoch);
        Assert.Equal("owner-b", evidence.Snapshot.Ownership.OwnerId);
    }

    [Fact]
    public async Task Admission_clock_rollback_before_latest_heartbeat_halts_even_at_maximum_heartbeat_interval()
    {
        var firstWorkEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var evidence = new RecordingCoordinatorEvidencePort();
        var clock = Clock();
        var observer = new SignalingCoordinatorBoundaryObserver();
        var work = new ScriptedLocalWorkRunner
        {
            Handler = async (_, _) =>
            {
                firstWorkEntered.TrySetResult();
                await releaseFirstWork.Task;
                return new GovernedLoopLocalWorkResult(GovernedLoopLocalWorkResultStatus.Completed, "safe-boundary");
            }
        };
        await using var coordinator = Coordinator(
            evidence,
            work,
            clock,
            "owner-a",
            cycle: TimeSpan.FromMilliseconds(1),
            heartbeat: TimeSpan.FromDays(1) - TimeSpan.FromTicks(1),
            lease: TimeSpan.FromDays(1));
        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Started, (await coordinator.StartAsync()).Status);
        await firstWorkEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        clock.Advance(TimeSpan.FromDays(-1));
        releaseFirstWork.TrySetResult();
        await WaitUntilAsync(() => evidence.Snapshot?.LatestLifecycle.Status == GovernedLoopCoordinatorStatus.Failed);
        var stopped = await coordinator.StopAsync();

        Assert.Equal(GovernedLoopLocalCoordinatorStopStatus.Failed, stopped.Status);
        Assert.Equal(1, work.CallCount);
        var failure = Assert.Single(evidence.Failures);
        Assert.Equal(GovernedLoopCoordinatorFailureKind.Unexpected, failure.Kind);
        Assert.Equal("work-admission-clock-rollback", failure.DetailEvidenceReference);
    }

    [Fact]
    public async Task Live_stopped_owner_remains_exclusive_until_its_heartbeat_lease_expires()
    {
        var evidence = new RecordingCoordinatorEvidencePort();
        var clock = Clock();
        await using var first = Coordinator(
            evidence,
            new ScriptedLocalWorkRunner(),
            clock,
            "owner-a",
            heartbeat: TimeSpan.FromSeconds(1),
            lease: TimeSpan.FromSeconds(5));
        await first.StartAsync();
        await first.StopAsync();
        await using var second = Coordinator(
            evidence,
            new ScriptedLocalWorkRunner(),
            clock,
            "owner-b",
            heartbeat: TimeSpan.FromSeconds(1),
            lease: TimeSpan.FromSeconds(5));

        var result = await second.StartAsync();

        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.OwnedByLivePeer, result.Status);
        Assert.Equal("owner-a", result.Snapshot!.Ownership.OwnerId);
    }

    [Fact]
    public async Task Confirmed_local_stopped_session_restarts_immediately_with_a_fenced_same_owner_successor()
    {
        var evidence = new RecordingCoordinatorEvidencePort();
        await using var coordinator = Coordinator(evidence, new ScriptedLocalWorkRunner(), Clock(), "owner-a");

        var initial = await coordinator.StartAsync();
        var stopped = await coordinator.StopAsync();
        var restarted = await coordinator.StartAsync();
        var snapshot = evidence.Snapshot;

        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Started, initial.Status);
        Assert.Equal(GovernedLoopLocalCoordinatorStopStatus.Stopped, stopped.Status);
        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Started, restarted.Status);
        Assert.Equal("owner-a", snapshot!.Ownership.OwnerId);
        Assert.Equal(2, snapshot.Ownership.OwnershipEpoch);
        Assert.Equal(GovernedLoopCoordinatorStatus.Running, snapshot.LatestLifecycle.Status);
        Assert.True(snapshot.LatestHeartbeat.LeaseExpiresAtUtc > snapshot.LatestHeartbeat.RecordedAtUtc);

        Assert.Equal(GovernedLoopLocalCoordinatorStopStatus.Stopped, (await coordinator.StopAsync()).Status);
    }

    [Fact]
    public async Task Shutdown_waits_for_hostile_one_shot_safe_boundary_before_recording_stopped()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var evidence = new RecordingCoordinatorEvidencePort();
        var work = new ScriptedLocalWorkRunner
        {
            Handler = async (family, _) =>
            {
                if (family == GovernedLoopLocalWorkFamily.Schedule)
                {
                    entered.TrySetResult();
                    await release.Task;
                    return new GovernedLoopLocalWorkResult(GovernedLoopLocalWorkResultStatus.Completed, "safe-boundary");
                }

                return new GovernedLoopLocalWorkResult(GovernedLoopLocalWorkResultStatus.Empty, "no-work");
            }
        };
        await using var coordinator = Coordinator(evidence, work, Clock(), "owner-a");
        await coordinator.StartAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stopping = coordinator.StopAsync();
        await Task.Delay(50);

        Assert.False(stopping.IsCompleted);
        Assert.Equal(GovernedLoopCoordinatorStatus.Stopping, evidence.Snapshot!.LatestLifecycle.Status);
        Assert.DoesNotContain(evidence.Lifecycles, item => item.Status == GovernedLoopCoordinatorStatus.Stopped);

        release.TrySetResult();
        Assert.Equal(GovernedLoopLocalCoordinatorStopStatus.Stopped, (await stopping).Status);
        Assert.Equal(GovernedLoopCoordinatorStatus.Stopped, evidence.Snapshot!.LatestLifecycle.Status);
    }

    [Fact]
    public async Task Malformed_one_shot_result_fails_closed_with_bounded_corruption_evidence()
    {
        var evidence = new RecordingCoordinatorEvidencePort();
        var work = new ScriptedLocalWorkRunner
        {
            Handler = static (_, _) => Task.FromResult<GovernedLoopLocalWorkResult?>(null)
        };
        await using var coordinator = Coordinator(evidence, work, Clock(), "owner-a");

        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Started, (await coordinator.StartAsync()).Status);
        await WaitUntilAsync(() => evidence.Snapshot?.LatestLifecycle.Status == GovernedLoopCoordinatorStatus.Failed);
        var stopped = await coordinator.StopAsync();

        Assert.Equal(GovernedLoopLocalCoordinatorStopStatus.Failed, stopped.Status);
        Assert.Single(evidence.Failures);
        Assert.Equal(GovernedLoopCoordinatorFailureKind.CorruptState, evidence.Failures[0].Kind);
        Assert.Equal("schedule-result-corrupt", evidence.Failures[0].DetailEvidenceReference);
    }

    [Fact]
    public async Task Completed_failed_session_is_reaped_and_never_reports_ready_already_running_posture()
    {
        var evidence = new RecordingCoordinatorEvidencePort();
        var work = new ScriptedLocalWorkRunner
        {
            Handler = static (_, _) => Task.FromResult<GovernedLoopLocalWorkResult?>(null)
        };
        await using var coordinator = Coordinator(evidence, work, Clock(), "owner-a");

        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Started, (await coordinator.StartAsync()).Status);
        await WaitUntilAsync(() => evidence.Snapshot?.LatestLifecycle.Status == GovernedLoopCoordinatorStatus.Failed);
        var afterFailure = await coordinator.StartAsync();
        var durable = evidence.Snapshot;

        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Failed, afterFailure.Status);
        Assert.Equal(GovernedLoopCoordinatorStatus.Failed, durable!.LatestLifecycle.Status);
        Assert.Equal(durable, afterFailure.Snapshot);
        Assert.Single(evidence.Failures);
    }

    [Fact]
    public async Task Durably_failed_uncompleted_session_never_reports_ready_already_running_posture()
    {
        var evidence = new RecordingCoordinatorEvidencePort();
        var blockingEvidence = new BlockingCoordinatorEvidencePort(evidence)
        {
            BlockFailureBeforeCommit = false,
            BlockFailedLifecycleAfterCommit = true
        };
        var work = new ScriptedLocalWorkRunner
        {
            Handler = static (_, _) => Task.FromResult<GovernedLoopLocalWorkResult?>(null)
        };
        await using var coordinator = Coordinator(blockingEvidence, work, Clock(), "owner-a");

        try
        {
            Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Started, (await coordinator.StartAsync()).Status);
            await blockingEvidence.FailedLifecyclePersisted.WaitAsync(TimeSpan.FromSeconds(5));
            var durable = evidence.Snapshot;
            var duringFailure = await coordinator.StartAsync();

            Assert.Equal(GovernedLoopCoordinatorStatus.Failed, durable!.LatestLifecycle.Status);
            Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Failed, duringFailure.Status);
            Assert.Equal(durable, duringFailure.Snapshot);
            Assert.Single(evidence.Failures);
        }
        finally
        {
            blockingEvidence.ReleaseFailedLifecycle();
        }

        Assert.Equal(GovernedLoopLocalCoordinatorStopStatus.Failed, (await coordinator.StopAsync()).Status);
    }

    [Fact]
    public async Task Backpressured_work_after_foreign_heartbeat_never_mutates_peer_evidence()
    {
        var workEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workRelease = new TaskCompletionSource<GovernedLoopLocalWorkResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var evidence = new RecordingCoordinatorEvidencePort();
        var observer = new SignalingCoordinatorBoundaryObserver
        {
            ThrowOnOwnershipLost = true,
            ThrowOnForeignSessionMutationSuppressed = true
        };
        var work = new ScriptedLocalWorkRunner
        {
            Handler = (_, _) =>
            {
                workEntered.TrySetResult();
                return workRelease.Task;
            }
        };
        await using var coordinator = Coordinator(
            evidence,
            work,
            Clock(),
            "owner-a",
            heartbeat: TimeSpan.FromMilliseconds(10),
            lease: TimeSpan.FromMinutes(1),
            boundaryObserver: observer);

        try
        {
            Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Started, (await coordinator.StartAsync()).Status);
            await workEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var peer = evidence.ReplaceWithPeerOwnership("peer-owner");
            var lifecycleCount = evidence.Lifecycles.Count;
            var heartbeatCount = evidence.Heartbeats.Count;
            var failureCount = evidence.Failures.Count;

            await observer.OwnershipLost.WaitAsync(TimeSpan.FromSeconds(5));
            workRelease.TrySetResult(new GovernedLoopLocalWorkResult(
                GovernedLoopLocalWorkResultStatus.Backpressured,
                "bounded-pressure"));
            await observer.ForeignSessionMutationSuppressed.WaitAsync(TimeSpan.FromSeconds(5));
            var stopped = await coordinator.StopAsync();
            var durable = evidence.Snapshot;

            Assert.Equal(GovernedLoopLocalCoordinatorStopStatus.OwnershipLost, stopped.Status);
            Assert.Equal(peer.Ownership, durable!.Ownership);
            Assert.Equal(peer.LatestLifecycle, durable.LatestLifecycle);
            Assert.Equal(peer.LatestHeartbeat, durable.LatestHeartbeat);
            Assert.Equal(peer.LatestFailureSequence, durable.LatestFailureSequence);
            Assert.Equal(peer.LatestFailureHash, durable.LatestFailureHash);
            Assert.Equal(lifecycleCount, evidence.Lifecycles.Count);
            Assert.Equal(heartbeatCount, evidence.Heartbeats.Count);
            Assert.Equal(failureCount, evidence.Failures.Count);
        }
        finally
        {
            workRelease.TrySetResult(new GovernedLoopLocalWorkResult(
                GovernedLoopLocalWorkResultStatus.Backpressured,
                "bounded-pressure"));
        }
    }

    [Fact]
    public async Task Public_stop_parks_a_session_after_foreign_ownership_is_observed()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<GovernedLoopLocalWorkResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var evidence = new RecordingCoordinatorEvidencePort();
        var observer = new SignalingCoordinatorBoundaryObserver();
        var work = new ScriptedLocalWorkRunner
        {
            Handler = (_, _) =>
            {
                entered.TrySetResult();
                return release.Task;
            }
        };
        await using var coordinator = Coordinator(
            evidence,
            work,
            Clock(),
            "owner-a",
            heartbeat: TimeSpan.FromMilliseconds(10),
            lease: TimeSpan.FromMinutes(1),
            boundaryObserver: observer);

        try
        {
            Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Started, (await coordinator.StartAsync()).Status);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            evidence.ReplaceWithPeerOwnership("peer-owner");
            await observer.OwnershipLost.WaitAsync(TimeSpan.FromSeconds(5));

            var stopping = coordinator.StopAsync();
            await Task.Delay(25);
            Assert.False(stopping.IsCompleted);
            release.TrySetResult(new GovernedLoopLocalWorkResult(GovernedLoopLocalWorkResultStatus.Empty, "safe-boundary"));

            Assert.Equal(GovernedLoopLocalCoordinatorStopStatus.OwnershipLost, (await stopping).Status);
            Assert.Equal("peer-owner", evidence.Snapshot!.Ownership.OwnerId);
        }
        finally
        {
            release.TrySetResult(new GovernedLoopLocalWorkResult(GovernedLoopLocalWorkResultStatus.Empty, "safe-boundary"));
        }
    }

    [Fact]
    public async Task Heartbeat_store_failure_cancels_acquisition_and_fails_without_stopped_fabrication()
    {
        var evidence = new RecordingCoordinatorEvidencePort { ThrowOnHeartbeat = true };
        await using var coordinator = Coordinator(
            evidence,
            new ScriptedLocalWorkRunner(),
            Clock(),
            "owner-a",
            heartbeat: TimeSpan.FromMilliseconds(10));

        await coordinator.StartAsync();
        await WaitUntilAsync(() => evidence.Snapshot?.LatestLifecycle.Status == GovernedLoopCoordinatorStatus.Failed);
        var stopped = await coordinator.StopAsync();

        Assert.Equal(GovernedLoopLocalCoordinatorStopStatus.Failed, stopped.Status);
        Assert.Contains(evidence.Failures, item => item.Kind == GovernedLoopCoordinatorFailureKind.StoreUnavailable);
        Assert.DoesNotContain(evidence.Lifecycles, item => item.Status == GovernedLoopCoordinatorStatus.Stopped);
    }

    [Fact]
    public async Task Heartbeat_samples_time_after_evidence_gate_wait_and_never_renews_an_expired_lease()
    {
        var evidence = new RecordingCoordinatorEvidencePort();
        var blockingEvidence = new BlockingCoordinatorEvidencePort(evidence);
        var work = new ScriptedLocalWorkRunner
        {
            Handler = static (_, _) => Task.FromResult<GovernedLoopLocalWorkResult?>(
                new GovernedLoopLocalWorkResult(GovernedLoopLocalWorkResultStatus.Backpressured, "bounded-pressure"))
        };
        var clock = Clock();
        var boundaryObserver = new SignalingCoordinatorBoundaryObserver();
        await using var coordinator = Coordinator(
            blockingEvidence,
            work,
            clock,
            "owner-a",
            heartbeat: TimeSpan.FromMilliseconds(10),
            lease: TimeSpan.FromMilliseconds(100),
            boundaryObserver: boundaryObserver);

        await coordinator.StartAsync();
        await blockingEvidence.FailureEntered.WaitAsync(TimeSpan.FromSeconds(5));
        await boundaryObserver.HeartbeatDue.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(1));
        blockingEvidence.ReleaseFailure();
        await WaitUntilAsync(() => evidence.Snapshot?.LatestLifecycle.Status == GovernedLoopCoordinatorStatus.Failed);

        var result = await coordinator.StopAsync();

        Assert.Equal(GovernedLoopLocalCoordinatorStopStatus.Failed, result.Status);
        Assert.Single(evidence.Heartbeats);
        Assert.Contains(evidence.Failures, item => item.Kind == GovernedLoopCoordinatorFailureKind.HeartbeatExpired);
    }

    [Fact]
    public async Task Hostile_boundary_observer_cannot_suppress_heartbeat_or_change_durable_posture()
    {
        var evidence = new RecordingCoordinatorEvidencePort();
        var observer = new ThrowingCoordinatorBoundaryObserver();
        await using var coordinator = Coordinator(
            evidence,
            new ScriptedLocalWorkRunner(),
            Clock(),
            "owner-a",
            heartbeat: TimeSpan.FromMilliseconds(10),
            boundaryObserver: observer);

        await coordinator.StartAsync();
        await WaitUntilAsync(() => evidence.Heartbeats.Count >= 2);
        var result = await coordinator.StopAsync();

        Assert.True(observer.Calls > 0);
        Assert.Equal(GovernedLoopLocalCoordinatorStopStatus.Stopped, result.Status);
        Assert.Equal(GovernedLoopCoordinatorStatus.Stopped, evidence.Snapshot!.LatestLifecycle.Status);
    }

    [Fact]
    public async Task Repeated_backpressure_records_one_failure_per_uninterrupted_family_episode()
    {
        var evidence = new RecordingCoordinatorEvidencePort();
        var work = new ScriptedLocalWorkRunner
        {
            Handler = static (_, _) => Task.FromResult<GovernedLoopLocalWorkResult?>(
                new GovernedLoopLocalWorkResult(GovernedLoopLocalWorkResultStatus.Backpressured, "bounded-pressure"))
        };
        await using var coordinator = Coordinator(
            evidence,
            work,
            Clock(),
            "owner-a",
            cycle: TimeSpan.FromMilliseconds(5));

        await coordinator.StartAsync();
        await WaitUntilAsync(() => work.CallCount >= 9);
        await coordinator.StopAsync();

        Assert.Equal(3, evidence.Failures.Count);
        Assert.All(evidence.Failures, item => Assert.Equal(GovernedLoopCoordinatorFailureKind.Backpressured, item.Kind));
    }

    [Theory]
    [InlineData((int)CoordinatorPostCommitFailureMode.ReturnUnavailable)]
    [InlineData((int)CoordinatorPostCommitFailureMode.Throw)]
    public async Task Post_commit_acquisition_failure_reconciles_exact_evidence_before_admitting_work(
        int failureMode)
    {
        var evidence = new RecordingCoordinatorEvidencePort { AcquisitionPostCommitFailure = (CoordinatorPostCommitFailureMode)failureMode };
        var work = new ScriptedLocalWorkRunner();
        await using var coordinator = Coordinator(evidence, work, Clock(), "owner-a");

        var started = await coordinator.StartAsync();
        await WaitUntilAsync(() => work.CallCount > 0);
        var stopped = await coordinator.StopAsync();

        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Started, started.Status);
        Assert.Equal(GovernedLoopLocalCoordinatorStopStatus.Stopped, stopped.Status);
        Assert.Equal("owner-a", evidence.Snapshot!.Ownership.OwnerId);
        Assert.Equal(1, evidence.Snapshot.Ownership.OwnershipEpoch);
    }

    [Fact]
    public async Task Post_commit_acquisition_reconciliation_rejects_a_newer_lifecycle_head()
    {
        var evidence = new RecordingCoordinatorEvidencePort
        {
            AcquisitionPostCommitFailure = CoordinatorPostCommitFailureMode.ReturnUnavailable,
            AdvanceAcquisitionBeforePostCommitFailure = true
        };
        var work = new ScriptedLocalWorkRunner();
        await using var coordinator = Coordinator(evidence, work, Clock(), "owner-a");

        var started = await coordinator.StartAsync();

        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Unavailable, started.Status);
        Assert.Equal(GovernedLoopCoordinatorStatus.Running, evidence.Snapshot!.LatestLifecycle.Status);
        Assert.Equal(0, work.CallCount);
    }

    [Theory]
    [InlineData((int)CoordinatorPostCommitFailureMode.ReturnUnavailable)]
    [InlineData((int)CoordinatorPostCommitFailureMode.Throw)]
    public async Task Post_commit_running_lifecycle_failure_reconciles_only_the_exact_successor(
        int failureMode)
    {
        var evidence = new RecordingCoordinatorEvidencePort { LifecyclePostCommitFailure = (CoordinatorPostCommitFailureMode)failureMode };
        var work = new ScriptedLocalWorkRunner();
        await using var coordinator = Coordinator(evidence, work, Clock(), "owner-a");

        var started = await coordinator.StartAsync();
        await WaitUntilAsync(() => work.CallCount > 0);
        var stopped = await coordinator.StopAsync();

        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Started, started.Status);
        Assert.Contains(evidence.Lifecycles, item => item.Status == GovernedLoopCoordinatorStatus.Running);
        Assert.Equal(GovernedLoopLocalCoordinatorStopStatus.Stopped, stopped.Status);
    }

    [Fact]
    public async Task Post_commit_reconciliation_does_not_accept_a_newer_lifecycle_head()
    {
        var evidence = new RecordingCoordinatorEvidencePort
        {
            LifecyclePostCommitFailure = CoordinatorPostCommitFailureMode.ReturnUnavailable,
            AdvanceLifecycleBeforePostCommitFailure = true
        };
        var work = new ScriptedLocalWorkRunner();
        await using var coordinator = Coordinator(evidence, work, Clock(), "owner-a");

        var started = await coordinator.StartAsync();

        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Unavailable, started.Status);
        Assert.Equal(GovernedLoopCoordinatorStatus.Stopping, evidence.Snapshot!.LatestLifecycle.Status);
        Assert.Equal(0, work.CallCount);
    }

    [Theory]
    [InlineData((int)CoordinatorPostCommitFailureMode.ReturnUnavailable)]
    [InlineData((int)CoordinatorPostCommitFailureMode.Throw)]
    public async Task Post_commit_heartbeat_failure_reconciles_the_exact_lease_extension(
        int failureMode)
    {
        var evidence = new RecordingCoordinatorEvidencePort { HeartbeatPostCommitFailure = (CoordinatorPostCommitFailureMode)failureMode };
        await using var coordinator = Coordinator(
            evidence,
            new ScriptedLocalWorkRunner(),
            Clock(),
            "owner-a",
            heartbeat: TimeSpan.FromMilliseconds(10));

        await coordinator.StartAsync();
        await WaitUntilAsync(() => evidence.Heartbeats.Count >= 2);
        var stopped = await coordinator.StopAsync();

        Assert.Equal(GovernedLoopLocalCoordinatorStopStatus.Stopped, stopped.Status);
        Assert.DoesNotContain(evidence.Failures, item => item.Kind == GovernedLoopCoordinatorFailureKind.StoreUnavailable);
    }

    [Fact]
    public async Task Post_commit_heartbeat_reconciliation_rejects_a_newer_lease_head()
    {
        var evidence = new RecordingCoordinatorEvidencePort
        {
            HeartbeatPostCommitFailure = CoordinatorPostCommitFailureMode.ReturnUnavailable,
            AdvanceHeartbeatBeforePostCommitFailure = true
        };
        await using var coordinator = Coordinator(
            evidence,
            new ScriptedLocalWorkRunner(),
            Clock(),
            "owner-a",
            heartbeat: TimeSpan.FromMilliseconds(10));

        await coordinator.StartAsync();
        await WaitUntilAsync(() => evidence.Lifecycles.Any(item => item.Status == GovernedLoopCoordinatorStatus.Failed));
        var stopped = await coordinator.StopAsync();

        Assert.Equal(GovernedLoopLocalCoordinatorStopStatus.Failed, stopped.Status);
        Assert.Contains(evidence.Failures, item => item.Kind == GovernedLoopCoordinatorFailureKind.StoreUnavailable);
        Assert.True(evidence.Snapshot!.LatestHeartbeat.HeartbeatSequence >= 3);
    }

    [Theory]
    [InlineData((int)CoordinatorPostCommitFailureMode.ReturnUnavailable)]
    [InlineData((int)CoordinatorPostCommitFailureMode.Throw)]
    public async Task Post_commit_failure_evidence_failure_reconciles_the_exact_append(
        int failureMode)
    {
        var evidence = new RecordingCoordinatorEvidencePort { FailurePostCommitFailure = (CoordinatorPostCommitFailureMode)failureMode };
        var work = new ScriptedLocalWorkRunner
        {
            Handler = static (family, _) => Task.FromResult<GovernedLoopLocalWorkResult?>(
                new GovernedLoopLocalWorkResult(
                    family == GovernedLoopLocalWorkFamily.Schedule
                        ? GovernedLoopLocalWorkResultStatus.Backpressured
                        : GovernedLoopLocalWorkResultStatus.Empty,
                    "bounded-posture"))
        };
        await using var coordinator = Coordinator(evidence, work, Clock(), "owner-a");

        await coordinator.StartAsync();
        await WaitUntilAsync(() => evidence.Failures.Count == 1);
        var stopped = await coordinator.StopAsync();

        Assert.Equal(GovernedLoopLocalCoordinatorStopStatus.Stopped, stopped.Status);
        Assert.Equal(GovernedLoopCoordinatorFailureKind.Backpressured, Assert.Single(evidence.Failures).Kind);
        Assert.Equal(evidence.Failures[0].ContentHash, evidence.Snapshot!.LatestFailureHash);
    }

    [Fact]
    public async Task Post_commit_failure_reconciliation_rejects_a_newer_failure_head()
    {
        var evidence = new RecordingCoordinatorEvidencePort
        {
            FailurePostCommitFailure = CoordinatorPostCommitFailureMode.ReturnUnavailable,
            AdvanceFailureBeforePostCommitFailure = true
        };
        var work = new ScriptedLocalWorkRunner
        {
            Handler = static (family, _) => Task.FromResult<GovernedLoopLocalWorkResult?>(
                new GovernedLoopLocalWorkResult(
                    family == GovernedLoopLocalWorkFamily.Schedule
                        ? GovernedLoopLocalWorkResultStatus.Backpressured
                        : GovernedLoopLocalWorkResultStatus.Empty,
                    "bounded-posture"))
        };
        await using var coordinator = Coordinator(evidence, work, Clock(), "owner-a");

        await coordinator.StartAsync();
        await WaitUntilAsync(() => evidence.Failures.Count >= 2);
        var stopped = await coordinator.StopAsync();

        Assert.Equal(GovernedLoopLocalCoordinatorStopStatus.OwnershipLost, stopped.Status);
        Assert.Equal(2, evidence.Snapshot!.LatestFailureSequence);
        Assert.Equal(evidence.Failures[1].ContentHash, evidence.Snapshot.LatestFailureHash);
    }

    [Fact]
    public async Task Malformed_durable_read_fails_start_closed_without_admitting_work()
    {
        var evidence = new RecordingCoordinatorEvidencePort { ReturnMalformedRead = true };
        var work = new ScriptedLocalWorkRunner();
        await using var coordinator = Coordinator(evidence, work, Clock(), "owner-a");

        var result = await coordinator.StartAsync();

        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Corrupt, result.Status);
        Assert.Equal(0, work.CallCount);
        Assert.Equal(GovernedLoopLocalCoordinatorStopStatus.AlreadyStopped, (await coordinator.StopAsync()).Status);
    }

    [Fact]
    public async Task Acquisition_without_exact_durable_snapshot_fails_closed_before_work_admission()
    {
        var evidence = new RecordingCoordinatorEvidencePort
        {
            AcquisitionOverride = GovernedLoopCoordinatorAcquisitionStatus.Acquired
        };
        var work = new ScriptedLocalWorkRunner();
        await using var coordinator = Coordinator(evidence, work, Clock(), "owner-a");

        var result = await coordinator.StartAsync();

        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Corrupt, result.Status);
        Assert.Equal(0, work.CallCount);
    }

    [Fact]
    public async Task Acquisition_with_a_mismatched_durable_snapshot_fails_closed_before_work_admission()
    {
        var evidence = new RecordingCoordinatorEvidencePort();
        var clock = Clock();
        await using (var seed = Coordinator(evidence, new ScriptedLocalWorkRunner(), clock, "seed-owner"))
        {
            Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Started, (await seed.StartAsync()).Status);
            Assert.Equal(GovernedLoopLocalCoordinatorStopStatus.Stopped, (await seed.StopAsync()).Status);
        }

        clock.Advance(TimeSpan.FromMinutes(3));
        evidence.AcquisitionOverride = GovernedLoopCoordinatorAcquisitionStatus.Acquired;
        var work = new ScriptedLocalWorkRunner();
        await using var coordinator = Coordinator(evidence, work, clock, "owner-a");

        var result = await coordinator.StartAsync();

        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Corrupt, result.Status);
        Assert.Equal(0, work.CallCount);
    }

    [Fact]
    public async Task Null_acquisition_result_is_projected_as_corrupt_without_admitting_work()
    {
        var evidence = new RecordingCoordinatorEvidencePort { ReturnNullAcquisition = true };
        var work = new ScriptedLocalWorkRunner();
        await using var coordinator = Coordinator(evidence, work, Clock(), "owner-a");

        var result = await coordinator.StartAsync();

        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Corrupt, result.Status);
        Assert.Equal(0, work.CallCount);
    }

    [Fact]
    public async Task Coordinator_read_exception_is_projected_as_unavailable()
    {
        var evidence = new RecordingCoordinatorEvidencePort { ThrowOnRead = true };
        await using var coordinator = Coordinator(evidence, new ScriptedLocalWorkRunner(), Clock(), "owner-a");

        var result = await coordinator.StartAsync();

        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task Coordinator_read_cancellation_is_rethrown_before_acquisition()
    {
        using var cancellation = new CancellationTokenSource();
        var evidence = new RecordingCoordinatorEvidencePort
        {
            CancelOnRead = true,
            CancelSourceOnRead = cancellation
        };
        await using var coordinator = Coordinator(evidence, new ScriptedLocalWorkRunner(), Clock(), "owner-a");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.StartAsync(cancellation.Token));
    }

    [Fact]
    public async Task Coordinator_acquisition_exception_is_reconciled_as_unavailable()
    {
        var evidence = new RecordingCoordinatorEvidencePort { ThrowOnAcquire = true };
        await using var coordinator = Coordinator(evidence, new ScriptedLocalWorkRunner(), Clock(), "owner-a");

        var result = await coordinator.StartAsync();

        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task Coordinator_acquisition_cancellation_is_rethrown_when_evidence_did_not_commit()
    {
        using var cancellation = new CancellationTokenSource();
        var evidence = new RecordingCoordinatorEvidencePort
        {
            CancelOnAcquire = true,
            CancelSourceOnAcquire = cancellation
        };
        await using var coordinator = Coordinator(evidence, new ScriptedLocalWorkRunner(), Clock(), "owner-a");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.StartAsync(cancellation.Token));
    }

    [Fact]
    public async Task Coordinator_acquisition_cancellation_after_durable_commit_reconciles_exact_evidence()
    {
        using var cancellation = new CancellationTokenSource();
        var evidence = new RecordingCoordinatorEvidencePort
        {
            CancelAfterAcquire = true,
            CancelSourceOnAcquire = cancellation
        };
        var work = new ScriptedLocalWorkRunner
        {
            Handler = static (_, _) => Task.FromResult<GovernedLoopLocalWorkResult?>(
                new GovernedLoopLocalWorkResult(GovernedLoopLocalWorkResultStatus.Backpressured, "bounded-pressure"))
        };
        await using var coordinator = Coordinator(evidence, work, Clock(), "owner-a");

        var started = await coordinator.StartAsync(cancellation.Token);
        var stopped = await coordinator.StopAsync();

        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Started, started.Status);
        Assert.Equal(GovernedLoopLocalCoordinatorStopStatus.Stopped, stopped.Status);
        Assert.NotEmpty(evidence.Lifecycles);
    }

    [Fact]
    public async Task Null_lifecycle_result_fails_start_closed_after_acquisition()
    {
        var evidence = new RecordingCoordinatorEvidencePort { ReturnNullLifecycle = true };
        await using var coordinator = Coordinator(evidence, new ScriptedLocalWorkRunner(), Clock(), "owner-a");

        var result = await coordinator.StartAsync();

        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Corrupt, result.Status);
        Assert.Equal(0, evidence.Lifecycles.Count(item => item.Status == GovernedLoopCoordinatorStatus.Running));
    }

    [Fact]
    public async Task Start_rechecks_an_uncompleted_session_when_durable_evidence_becomes_corrupt()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<GovernedLoopLocalWorkResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var evidence = new RecordingCoordinatorEvidencePort();
        var work = new ScriptedLocalWorkRunner
        {
            Handler = (_, _) =>
            {
                entered.TrySetResult();
                return release.Task;
            }
        };
        await using var coordinator = Coordinator(evidence, work, Clock(), "owner-a");

        try
        {
            Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Started, (await coordinator.StartAsync()).Status);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            evidence.ReturnMalformedRead = true;

            var result = await coordinator.StartAsync();

            Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Corrupt, result.Status);
        }
        finally
        {
            evidence.ReturnMalformedRead = false;
            release.TrySetResult(new GovernedLoopLocalWorkResult(GovernedLoopLocalWorkResultStatus.Empty, "safe-boundary"));
        }

        Assert.Equal(GovernedLoopLocalCoordinatorStopStatus.Stopped, (await coordinator.StopAsync()).Status);
    }

    [Fact]
    public async Task Start_rechecks_an_uncompleted_session_when_durable_evidence_is_terminal_stopped()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<GovernedLoopLocalWorkResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var evidence = new RecordingCoordinatorEvidencePort();
        var work = new ScriptedLocalWorkRunner
        {
            Handler = (_, _) =>
            {
                entered.TrySetResult();
                return release.Task;
            }
        };
        await using var coordinator = Coordinator(evidence, work, Clock(), "owner-a");

        try
        {
            Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Started, (await coordinator.StartAsync()).Status);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            evidence.SetLifecycleStatus(GovernedLoopCoordinatorStatus.Stopped);

            var result = await coordinator.StartAsync();

            Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Unavailable, result.Status);
        }
        finally
        {
            release.TrySetResult(new GovernedLoopLocalWorkResult(GovernedLoopLocalWorkResultStatus.Empty, "safe-boundary"));
        }

        Assert.Equal(GovernedLoopLocalCoordinatorStopStatus.Failed, (await coordinator.StopAsync()).Status);
    }

    [Fact]
    public async Task Null_failure_result_fails_closed_without_fabricating_terminal_lifecycle()
    {
        var evidence = new RecordingCoordinatorEvidencePort { ReturnNullFailure = true };
        var work = new ScriptedLocalWorkRunner
        {
            Handler = static (_, _) => Task.FromResult<GovernedLoopLocalWorkResult?>(
                new GovernedLoopLocalWorkResult(GovernedLoopLocalWorkResultStatus.Backpressured, "bounded-pressure"))
        };
        await using var coordinator = Coordinator(evidence, work, Clock(), "owner-a");

        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Started, (await coordinator.StartAsync()).Status);
        await WaitUntilAsync(() => work.CallCount > 0);
        var result = await coordinator.StopAsync();

        Assert.Equal(GovernedLoopLocalCoordinatorStopStatus.Failed, result.Status);
        Assert.DoesNotContain(evidence.Lifecycles, item => item.Status == GovernedLoopCoordinatorStatus.Stopped);
    }

    [Fact]
    public async Task Failure_evidence_snapshot_mismatch_fails_closed_without_stopped_evidence()
    {
        var evidence = new RecordingCoordinatorEvidencePort
        {
            FailureOverride = GovernedLoopCoordinatorFailureMutationStatus.Appended,
            ReturnMismatchedFailureSnapshot = true
        };
        var work = new ScriptedLocalWorkRunner
        {
            Handler = static (_, _) => Task.FromResult<GovernedLoopLocalWorkResult?>(
                new GovernedLoopLocalWorkResult(GovernedLoopLocalWorkResultStatus.Corrupt, "mismatched-failure-snapshot"))
        };
        await using var coordinator = Coordinator(evidence, work, Clock(), "owner-a");

        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Started, (await coordinator.StartAsync()).Status);
        await WaitUntilAsync(() => evidence.Snapshot?.LatestLifecycle.Status == GovernedLoopCoordinatorStatus.Failed);

        var result = await coordinator.StopAsync();

        Assert.Equal(GovernedLoopLocalCoordinatorStopStatus.Failed, result.Status);
        Assert.DoesNotContain(evidence.Lifecycles, item => item.Status == GovernedLoopCoordinatorStatus.Stopped);
    }

    [Fact]
    public async Task Start_rechecks_a_completed_session_when_durable_evidence_becomes_corrupt()
    {
        var evidence = new RecordingCoordinatorEvidencePort();
        var work = new ScriptedLocalWorkRunner
        {
            Handler = static (_, _) => Task.FromResult<GovernedLoopLocalWorkResult?>(null)
        };
        await using var coordinator = Coordinator(evidence, work, Clock(), "owner-a");

        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Started, (await coordinator.StartAsync()).Status);
        await WaitUntilAsync(() => evidence.Snapshot?.LatestLifecycle.Status == GovernedLoopCoordinatorStatus.Failed);
        evidence.ReturnMalformedRead = true;

        var result = await coordinator.StartAsync();

        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Corrupt, result.Status);
        evidence.ReturnMalformedRead = false;
    }

    [Fact]
    public async Task Start_reaps_a_completed_session_only_when_the_valid_durable_snapshot_is_unchanged()
    {
        var evidence = new RecordingCoordinatorEvidencePort();
        var work = new ScriptedLocalWorkRunner
        {
            Handler = static (_, _) => Task.FromResult<GovernedLoopLocalWorkResult?>(null)
        };
        await using var coordinator = Coordinator(evidence, work, Clock(), "owner-a");

        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Started, (await coordinator.StartAsync()).Status);
        await WaitUntilAsync(() => evidence.Snapshot?.LatestLifecycle.Status == GovernedLoopCoordinatorStatus.Failed);
        var current = Assert.IsType<GovernedLoopCoordinatorSnapshot>(evidence.Snapshot);
        evidence.SetLifecycleVersion(current.LatestLifecycle.LifecycleVersion + 1);

        var result = await coordinator.StartAsync();

        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Corrupt, result.Status);
        Assert.Equal(GovernedLoopCoordinatorStatus.Failed, result.Snapshot!.LatestLifecycle.Status);
        Assert.NotEqual(current.LatestLifecycle.ContentHash, result.Snapshot.LatestLifecycle.ContentHash);
    }

    [Fact]
    public async Task Heartbeat_result_mismatch_fails_closed_without_accepting_unfenced_lease()
    {
        var evidence = new RecordingCoordinatorEvidencePort
        {
            HeartbeatOverride = GovernedLoopCoordinatorHeartbeatMutationStatus.Renewed
        };
        await using var coordinator = Coordinator(
            evidence,
            new ScriptedLocalWorkRunner(),
            Clock(),
            "owner-a",
            heartbeat: TimeSpan.FromMilliseconds(10));

        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Started, (await coordinator.StartAsync()).Status);
        await WaitUntilAsync(() => evidence.Snapshot?.LatestLifecycle.Status == GovernedLoopCoordinatorStatus.Failed);
        var result = await coordinator.StopAsync();

        Assert.Equal(GovernedLoopLocalCoordinatorStopStatus.Failed, result.Status);
        Assert.Contains(evidence.Failures, item => item.DetailEvidenceReference == "heartbeat-result-mismatch");
    }

    [Theory]
    [InlineData(false, GovernedLoopCoordinatorFailureKind.Unexpected)]
    [InlineData(true, GovernedLoopCoordinatorFailureKind.CorruptState)]
    public async Task Invalid_one_shot_execution_is_retained_as_bounded_failure(
        bool invalidStatus,
        GovernedLoopCoordinatorFailureKind expectedKind)
    {
        var evidence = new RecordingCoordinatorEvidencePort();
        var work = new ScriptedLocalWorkRunner
        {
            Handler = invalidStatus
                ? static (_, _) => Task.FromResult<GovernedLoopLocalWorkResult?>(
                    new GovernedLoopLocalWorkResult((GovernedLoopLocalWorkResultStatus)99, "invalid-status"))
                : static (_, _) => throw new IOException("runner failed")
        };
        await using var coordinator = Coordinator(evidence, work, Clock(), "owner-a");

        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Started, (await coordinator.StartAsync()).Status);
        await WaitUntilAsync(() => evidence.Snapshot?.LatestLifecycle.Status == GovernedLoopCoordinatorStatus.Failed);
        var result = await coordinator.StopAsync();

        Assert.Equal(GovernedLoopLocalCoordinatorStopStatus.Failed, result.Status);
        Assert.Contains(evidence.Failures, item => item.Kind == expectedKind);
    }

    [Fact]
    public async Task Heartbeat_clock_failure_fails_closed_without_stopped_evidence()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<GovernedLoopLocalWorkResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var evidence = new RecordingCoordinatorEvidencePort();
        var clock = Clock();
        var observer = new SignalingCoordinatorBoundaryObserver();
        var work = new ScriptedLocalWorkRunner
        {
            Handler = (_, _) =>
            {
                entered.TrySetResult();
                return release.Task;
            }
        };
        await using var coordinator = Coordinator(
            evidence,
            work,
            clock,
            "owner-a",
            heartbeat: TimeSpan.FromMilliseconds(10),
            boundaryObserver: observer);

        try
        {
            Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Started, (await coordinator.StartAsync()).Status);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.ThrowOnNext = true;
            await observer.HeartbeatDue.WaitAsync(TimeSpan.FromSeconds(5));
            release.TrySetResult(new GovernedLoopLocalWorkResult(GovernedLoopLocalWorkResultStatus.Empty, "safe-boundary"));
            await WaitUntilAsync(() => evidence.Snapshot?.LatestLifecycle.Status == GovernedLoopCoordinatorStatus.Failed);
            var result = await coordinator.StopAsync();

            Assert.Equal(GovernedLoopLocalCoordinatorStopStatus.Failed, result.Status);
            Assert.Contains(evidence.Failures, item => item.DetailEvidenceReference == "heartbeat-clock-unavailable");
        }
        finally
        {
            release.TrySetResult(new GovernedLoopLocalWorkResult(GovernedLoopLocalWorkResultStatus.Empty, "safe-boundary"));
        }
    }

    [Fact]
    public async Task Work_admission_expiry_fails_closed_before_running_expired_work()
    {
        var evidence = new RecordingCoordinatorEvidencePort();
        var clock = Clock();
        await using var coordinator = Coordinator(
            evidence,
            new ScriptedLocalWorkRunner(),
            clock,
            "owner-a",
            cycle: TimeSpan.FromMilliseconds(20),
            heartbeat: TimeSpan.FromMilliseconds(50),
            lease: TimeSpan.FromMilliseconds(100));

        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Started, (await coordinator.StartAsync()).Status);
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => evidence.Snapshot?.LatestLifecycle.Status == GovernedLoopCoordinatorStatus.Failed);
        var result = await coordinator.StopAsync();

        Assert.Equal(GovernedLoopLocalCoordinatorStopStatus.Failed, result.Status);
        Assert.Contains(evidence.Failures, item => item.DetailEvidenceReference == "work-admission-lease-expired");
    }

    [Fact]
    public async Task Dispose_is_idempotent_and_rejects_later_start_or_stop_calls()
    {
        var coordinator = Coordinator(
            new RecordingCoordinatorEvidencePort(),
            new ScriptedLocalWorkRunner(),
            Clock(),
            "owner-a");
        await coordinator.StartAsync();

        await coordinator.DisposeAsync();
        await coordinator.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => coordinator.StartAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => coordinator.StopAsync());
    }

    [Theory]
    [InlineData("Bad", "owner-a")]
    [InlineData("coordinator", "Bad")]
    public void Constructor_rejects_noncanonical_coordinator_or_owner_identity(string coordinatorId, string ownerId)
    {
        var options = Options(ownerId) with { CoordinatorId = coordinatorId };

        Assert.Throws<ArgumentException>(() => new GovernedLoopLocalCoordinator(
            new RecordingCoordinatorEvidencePort(),
            new ScriptedLocalWorkRunner(),
            options,
            Clock()));
    }

    [Fact]
    public void Constructor_rejects_unbounded_cadence_and_fairness_options()
    {
        var invalid = Options("owner-a") with
        {
            HeartbeatInterval = TimeSpan.FromSeconds(2),
            OwnershipLeaseDuration = TimeSpan.FromSeconds(1),
            MaximumItemsPerFamilyPerCycle = 0
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopLocalCoordinator(
            new RecordingCoordinatorEvidencePort(),
            new ScriptedLocalWorkRunner(),
            invalid,
            Clock()));
    }

    [Theory]
    [InlineData(GovernedLoopCoordinatorAcquisitionStatus.OwnedByLivePeer, GovernedLoopLocalCoordinatorStartStatus.OwnedByLivePeer)]
    [InlineData(GovernedLoopCoordinatorAcquisitionStatus.LeaseNotExpired, GovernedLoopLocalCoordinatorStartStatus.OwnedByLivePeer)]
    [InlineData(GovernedLoopCoordinatorAcquisitionStatus.Conflict, GovernedLoopLocalCoordinatorStartStatus.Conflict)]
    [InlineData(GovernedLoopCoordinatorAcquisitionStatus.Corrupt, GovernedLoopLocalCoordinatorStartStatus.Corrupt)]
    [InlineData(GovernedLoopCoordinatorAcquisitionStatus.Unavailable, GovernedLoopLocalCoordinatorStartStatus.Unavailable)]
    public async Task Closed_acquisition_failures_are_projected_without_admitting_work(
        GovernedLoopCoordinatorAcquisitionStatus portStatus,
        GovernedLoopLocalCoordinatorStartStatus expectedStatus)
    {
        var evidence = new RecordingCoordinatorEvidencePort();
        var clock = Clock();
        await using (var seed = Coordinator(evidence, new ScriptedLocalWorkRunner(), clock, "seed-owner"))
        {
            await seed.StartAsync();
            await seed.StopAsync();
        }

        clock.Advance(TimeSpan.FromMinutes(3));
        evidence.AcquisitionOverride = portStatus;
        var work = new ScriptedLocalWorkRunner();
        await using var candidate = Coordinator(evidence, work, clock, "candidate-owner");

        var result = await candidate.StartAsync();

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(0, work.CallCount);
    }

    [Theory]
    [InlineData(GovernedLoopCoordinatorLifecycleMutationStatus.OwnershipLost, GovernedLoopLocalCoordinatorStartStatus.OwnedByLivePeer)]
    [InlineData(GovernedLoopCoordinatorLifecycleMutationStatus.Conflict, GovernedLoopLocalCoordinatorStartStatus.Conflict)]
    [InlineData(GovernedLoopCoordinatorLifecycleMutationStatus.Corrupt, GovernedLoopLocalCoordinatorStartStatus.Corrupt)]
    [InlineData(GovernedLoopCoordinatorLifecycleMutationStatus.Unavailable, GovernedLoopLocalCoordinatorStartStatus.Unavailable)]
    public async Task Running_lifecycle_failure_fails_start_closed(
        GovernedLoopCoordinatorLifecycleMutationStatus portStatus,
        GovernedLoopLocalCoordinatorStartStatus expectedStatus)
    {
        var evidence = new RecordingCoordinatorEvidencePort { LifecycleOverride = portStatus };
        var work = new ScriptedLocalWorkRunner();
        await using var coordinator = Coordinator(evidence, work, Clock(), "owner-a");

        var result = await coordinator.StartAsync();

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(0, work.CallCount);
    }

    [Theory]
    [InlineData(GovernedLoopCoordinatorLifecycleMutationStatus.OwnershipLost, GovernedLoopLocalCoordinatorStopStatus.OwnershipLost)]
    [InlineData(GovernedLoopCoordinatorLifecycleMutationStatus.Conflict, GovernedLoopLocalCoordinatorStopStatus.OwnershipLost)]
    [InlineData(GovernedLoopCoordinatorLifecycleMutationStatus.Corrupt, GovernedLoopLocalCoordinatorStopStatus.Failed)]
    [InlineData(GovernedLoopCoordinatorLifecycleMutationStatus.Unavailable, GovernedLoopLocalCoordinatorStopStatus.Unavailable)]
    public async Task Terminal_lifecycle_failure_is_never_projected_as_stopped(
        GovernedLoopCoordinatorLifecycleMutationStatus portStatus,
        GovernedLoopLocalCoordinatorStopStatus expectedStatus)
    {
        var evidence = new RecordingCoordinatorEvidencePort();
        await using var coordinator = Coordinator(evidence, new ScriptedLocalWorkRunner(), Clock(), "owner-a");
        await coordinator.StartAsync();
        evidence.LifecycleOverride = portStatus;

        var result = await coordinator.StopAsync();

        Assert.Equal(expectedStatus, result.Status);
        Assert.NotEqual(GovernedLoopCoordinatorStatus.Stopped, evidence.Snapshot!.LatestLifecycle.Status);
        Assert.Equal("owner-a", evidence.Snapshot.Ownership.OwnerId);
    }

    [Theory]
    [InlineData(GovernedLoopCoordinatorHeartbeatMutationStatus.OwnershipLost, GovernedLoopLocalCoordinatorStopStatus.OwnershipLost)]
    [InlineData(GovernedLoopCoordinatorHeartbeatMutationStatus.Conflict, GovernedLoopLocalCoordinatorStopStatus.OwnershipLost)]
    [InlineData(GovernedLoopCoordinatorHeartbeatMutationStatus.Corrupt, GovernedLoopLocalCoordinatorStopStatus.Failed)]
    [InlineData(GovernedLoopCoordinatorHeartbeatMutationStatus.Unavailable, GovernedLoopLocalCoordinatorStopStatus.Failed)]
    public async Task Heartbeat_failures_stop_new_work_and_preserve_closed_terminal_posture(
        GovernedLoopCoordinatorHeartbeatMutationStatus portStatus,
        GovernedLoopLocalCoordinatorStopStatus expectedStatus)
    {
        var evidence = new RecordingCoordinatorEvidencePort { HeartbeatOverride = portStatus };
        await using var coordinator = Coordinator(
            evidence,
            new ScriptedLocalWorkRunner(),
            Clock(),
            "owner-a",
            heartbeat: TimeSpan.FromMilliseconds(10));
        await coordinator.StartAsync();
        await WaitUntilAsync(() => evidence.HeartbeatAttempts >= 1);

        var result = await coordinator.StopAsync();

        Assert.Equal(expectedStatus, result.Status);
        Assert.DoesNotContain(evidence.Lifecycles, item => item.Status == GovernedLoopCoordinatorStatus.Stopped);
    }

    [Theory]
    [InlineData(GovernedLoopLocalWorkResultStatus.Unavailable, GovernedLoopCoordinatorFailureKind.StoreUnavailable)]
    [InlineData(GovernedLoopLocalWorkResultStatus.Corrupt, GovernedLoopCoordinatorFailureKind.CorruptState)]
    public async Task Fatal_one_shot_statuses_retain_exact_failure_category(
        GovernedLoopLocalWorkResultStatus workStatus,
        GovernedLoopCoordinatorFailureKind expectedKind)
    {
        var evidence = new RecordingCoordinatorEvidencePort();
        var work = new ScriptedLocalWorkRunner
        {
            Handler = (_, _) => Task.FromResult<GovernedLoopLocalWorkResult?>(
                new GovernedLoopLocalWorkResult(workStatus, "hostile-result"))
        };
        await using var coordinator = Coordinator(evidence, work, Clock(), "owner-a");
        await coordinator.StartAsync();
        await WaitUntilAsync(() => evidence.Snapshot?.LatestLifecycle.Status == GovernedLoopCoordinatorStatus.Failed);

        var result = await coordinator.StopAsync();

        Assert.Equal(GovernedLoopLocalCoordinatorStopStatus.Failed, result.Status);
        Assert.Contains(evidence.Failures, item => item.Kind == expectedKind);
    }

    [Fact]
    public async Task Failure_evidence_ownership_race_never_mutates_the_winning_owner_lifecycle()
    {
        var workEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWork = new TaskCompletionSource<GovernedLoopLocalWorkResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var evidence = new RecordingCoordinatorEvidencePort();
        var work = new ScriptedLocalWorkRunner
        {
            Handler = (_, _) =>
            {
                workEntered.TrySetResult();
                return releaseWork.Task;
            }
        };
        await using var coordinator = Coordinator(evidence, work, Clock(), "owner-a");

        try
        {
            Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Started, (await coordinator.StartAsync()).Status);
            await workEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var peer = evidence.ReplaceWithPeerOwnership("peer-owner");
            var installed = evidence.Snapshot;

            Assert.Equal(peer.Ownership, installed!.Ownership);
            Assert.Equal(peer.LatestLifecycle, installed.LatestLifecycle);
            Assert.Equal(peer.LatestHeartbeat, installed.LatestHeartbeat);
            Assert.Equal(peer.LatestFailureSequence, installed.LatestFailureSequence);
            Assert.Equal(peer.LatestFailureHash, installed.LatestFailureHash);
            Assert.Equal("peer-owner", installed.Ownership.OwnerId);
            Assert.Equal(2, installed.Ownership.OwnershipEpoch);
            Assert.Equal(GovernedLoopCoordinatorStatus.Starting, installed.LatestLifecycle.Status);

            releaseWork.TrySetResult(new GovernedLoopLocalWorkResult(
                GovernedLoopLocalWorkResultStatus.Corrupt,
                "hostile-result"));
            var result = await coordinator.StopAsync();
            var durable = evidence.Snapshot;

            Assert.Equal(GovernedLoopLocalCoordinatorStopStatus.OwnershipLost, result.Status);
            Assert.Equal(peer.Ownership, durable!.Ownership);
            Assert.Equal(peer.LatestLifecycle, durable.LatestLifecycle);
            Assert.Equal(peer.LatestHeartbeat, durable.LatestHeartbeat);
            Assert.Equal(peer.LatestFailureSequence, durable.LatestFailureSequence);
            Assert.Equal(peer.LatestFailureHash, durable.LatestFailureHash);
            Assert.Equal("peer-owner", durable.Ownership.OwnerId);
            Assert.Equal(2, durable.Ownership.OwnershipEpoch);
            Assert.Equal(GovernedLoopCoordinatorStatus.Starting, durable.LatestLifecycle.Status);
            Assert.DoesNotContain(evidence.Lifecycles, item => item.Status == GovernedLoopCoordinatorStatus.Failed);
        }
        finally
        {
            releaseWork.TrySetResult(new GovernedLoopLocalWorkResult(
                GovernedLoopLocalWorkResultStatus.Corrupt,
                "hostile-result"));
        }
    }

    [Theory]
    [InlineData(GovernedLoopCoordinatorFailureMutationStatus.Corrupt, GovernedLoopLocalCoordinatorStopStatus.Failed)]
    [InlineData(GovernedLoopCoordinatorFailureMutationStatus.Unavailable, GovernedLoopLocalCoordinatorStopStatus.Failed)]
    public async Task Failure_evidence_store_errors_fail_closed_without_stopped_evidence(
        GovernedLoopCoordinatorFailureMutationStatus failureStatus,
        GovernedLoopLocalCoordinatorStopStatus expectedStatus)
    {
        var evidence = new RecordingCoordinatorEvidencePort { FailureOverride = failureStatus };
        var work = new ScriptedLocalWorkRunner
        {
            Handler = static (_, _) => Task.FromResult<GovernedLoopLocalWorkResult?>(
                new GovernedLoopLocalWorkResult(GovernedLoopLocalWorkResultStatus.Corrupt, "hostile-result"))
        };
        await using var coordinator = Coordinator(evidence, work, Clock(), "owner-a");

        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Started, (await coordinator.StartAsync()).Status);
        await WaitUntilAsync(() => work.CallCount > 0);
        var result = await coordinator.StopAsync();

        Assert.Equal(expectedStatus, result.Status);
        Assert.DoesNotContain(evidence.Lifecycles, item => item.Status == GovernedLoopCoordinatorStatus.Stopped);
    }

    [Fact]
    public async Task Failed_durable_lifecycle_blocks_a_new_public_start()
    {
        var evidence = new RecordingCoordinatorEvidencePort();
        var clock = Clock();
        await using (var first = Coordinator(evidence, new ScriptedLocalWorkRunner(), clock, "owner-a"))
        {
            Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Started, (await first.StartAsync()).Status);
            await first.StopAsync();
        }

        evidence.SetLifecycleStatus(GovernedLoopCoordinatorStatus.Failed);
        clock.Advance(TimeSpan.FromMinutes(3));
        await using var second = Coordinator(evidence, new ScriptedLocalWorkRunner(), clock, "owner-b");

        var result = await second.StartAsync();

        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Failed, result.Status);
        Assert.Equal(GovernedLoopCoordinatorStatus.Failed, result.Snapshot!.LatestLifecycle.Status);
    }

    [Fact]
    public async Task Lifecycle_version_exhaustion_fails_closed_during_public_stop()
    {
        var evidence = new RecordingCoordinatorEvidencePort();
        await using var coordinator = Coordinator(evidence, new ScriptedLocalWorkRunner(), Clock(), "owner-a");

        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Started, (await coordinator.StartAsync()).Status);
        evidence.SetLifecycleVersion(GovernedLoopSleepContractLimits.MaxVersion);
        var result = await coordinator.StopAsync();

        Assert.Equal(GovernedLoopLocalCoordinatorStopStatus.Failed, result.Status);
        Assert.DoesNotContain(evidence.Lifecycles, item => item.Status == GovernedLoopCoordinatorStatus.Stopped);
    }

    [Fact]
    public async Task Invalid_heartbeat_result_fails_closed_without_stopped_evidence()
    {
        var evidence = new RecordingCoordinatorEvidencePort { ReturnNullHeartbeat = true };
        await using var coordinator = Coordinator(
            evidence,
            new ScriptedLocalWorkRunner(),
            Clock(),
            "owner-a",
            heartbeat: TimeSpan.FromMilliseconds(10));

        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Started, (await coordinator.StartAsync()).Status);
        await WaitUntilAsync(() => evidence.Snapshot?.LatestLifecycle.Status == GovernedLoopCoordinatorStatus.Failed);
        var result = await coordinator.StopAsync();

        Assert.Equal(GovernedLoopLocalCoordinatorStopStatus.Failed, result.Status);
        Assert.DoesNotContain(evidence.Lifecycles, item => item.Status == GovernedLoopCoordinatorStatus.Stopped);
    }

    [Fact]
    public async Task Throwing_clock_fails_initial_acquisition_closed()
    {
        var clock = Clock();
        clock.ThrowOnNext = true;
        var work = new ScriptedLocalWorkRunner();
        await using var coordinator = Coordinator(new RecordingCoordinatorEvidencePort(), work, clock, "owner-a");

        var result = await coordinator.StartAsync();

        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Corrupt, result.Status);
        Assert.Equal(0, work.CallCount);
    }

    [Fact]
    public async Task Acquisition_clock_overflow_fails_closed()
    {
        var clock = new SteppingCoordinatorTimeProvider(
            DateTimeOffset.MaxValue.AddHours(-1).ToUniversalTime(),
            TimeSpan.Zero);
        var options = Options("owner-a") with
        {
            HeartbeatInterval = TimeSpan.FromMinutes(30),
            OwnershipLeaseDuration = TimeSpan.FromHours(2)
        };
        await using var coordinator = new GovernedLoopLocalCoordinator(
            new RecordingCoordinatorEvidencePort(),
            new ScriptedLocalWorkRunner(),
            options,
            clock);

        var result = await coordinator.StartAsync();

        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Corrupt, result.Status);
    }

    [Fact]
    public async Task Same_owner_identity_cannot_bypass_expired_lease_handoff_fence()
    {
        var evidence = new RecordingCoordinatorEvidencePort();
        var clock = Clock();
        await using (var first = Coordinator(evidence, new ScriptedLocalWorkRunner(), clock, "owner-a"))
        {
            await first.StartAsync();
            await first.StopAsync();
        }

        clock.Advance(TimeSpan.FromMinutes(3));
        await using var replayedOwner = Coordinator(evidence, new ScriptedLocalWorkRunner(), clock, "owner-a");

        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Corrupt, (await replayedOwner.StartAsync()).Status);
    }

    [Fact]
    public async Task Exhausted_ownership_epoch_fails_closed_before_acquisition()
    {
        var evidence = new RecordingCoordinatorEvidencePort();
        var clock = Clock();
        await using (var first = Coordinator(evidence, new ScriptedLocalWorkRunner(), clock, "owner-a"))
        {
            await first.StartAsync();
            await first.StopAsync();
        }

        evidence.SetOwnershipEpoch(GovernedLoopSleepContractLimits.MaxVersion);
        clock.Advance(TimeSpan.FromMinutes(3));
        await using var next = Coordinator(evidence, new ScriptedLocalWorkRunner(), clock, "owner-b");

        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Corrupt, (await next.StartAsync()).Status);
    }

    [Fact]
    public async Task Regressing_shutdown_clock_preserves_monotonic_lifecycle_time()
    {
        var evidence = new RecordingCoordinatorEvidencePort();
        var clock = Clock();
        await using var coordinator = Coordinator(evidence, new ScriptedLocalWorkRunner(), clock, "owner-a");
        await coordinator.StartAsync();
        var runningAt = evidence.Snapshot!.LatestLifecycle.UpdatedAtUtc;
        clock.Advance(TimeSpan.FromDays(-1));

        await coordinator.StopAsync();

        Assert.All(
            evidence.Lifecycles.Where(item => item.Status is GovernedLoopCoordinatorStatus.Stopping or GovernedLoopCoordinatorStatus.Stopped),
            item => Assert.True(item.UpdatedAtUtc >= runningAt));
    }

    private static GovernedLoopLocalCoordinator Coordinator(
        IGovernedLoopCoordinatorEvidencePort evidence,
        ScriptedLocalWorkRunner work,
        SteppingCoordinatorTimeProvider clock,
        string ownerId,
        TimeSpan? cycle = null,
        TimeSpan? heartbeat = null,
        TimeSpan? lease = null,
        int perFamily = 1,
        IGovernedLoopLocalCoordinatorBoundaryObserver? boundaryObserver = null)
        => new(
            evidence,
            work,
            Options(ownerId) with
            {
                CycleInterval = cycle ?? TimeSpan.FromMilliseconds(20),
                HeartbeatInterval = heartbeat ?? TimeSpan.FromMinutes(1),
                OwnershipLeaseDuration = lease ?? TimeSpan.FromMinutes(2),
                MaximumItemsPerFamilyPerCycle = perFamily
            },
            clock,
            boundaryObserver);

    private static GovernedLoopLocalCoordinatorOptions Options(string ownerId)
        => new(
            "governed-loop-background",
            ownerId,
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(2),
            1);

    private static SteppingCoordinatorTimeProvider Clock()
        => new(_now, TimeSpan.FromMilliseconds(5));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(5, timeout.Token);
        }
    }
}
