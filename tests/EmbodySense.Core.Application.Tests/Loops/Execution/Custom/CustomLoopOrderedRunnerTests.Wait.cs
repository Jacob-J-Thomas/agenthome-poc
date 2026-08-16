using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.GraphAuthoring.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Application.Loops.Wait;
using EmbodySense.Core.Application.Loops.Wait.Models;
using EmbodySense.Core.Application.Tests.Capabilities;
using EmbodySense.Core.Application.Tests.Loops.Execution.Custom;
using EmbodySense.Core.Application.Tests.Loops.Sequential;
using EmbodySense.Core.Application.Tests.Loops.Sleep;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Common.Loops.Execution.Wait;
using EmbodySense.Core.Common.Loops.Execution.Wait.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Custom;

/// <summary>
/// Exercises Wait-specific behavior through the canonical ordered-runner public boundary.
/// </summary>
public sealed partial class CustomLoopOrderedRunnerTests
{
    [Fact]
    public async Task Canonical_timestamp_wait_parks_and_reenters_the_same_ordered_runtime_once()
    {
        var deadline = _now.AddMinutes(1);
        var context = await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role => TimestampWaitArtifact(role, deadline));
        var store = new FakeRunStore(context.Run);
        var time = new StubGovernedLoopSleepTimeProvider(_now);
        var sleepStore = new InMemoryGovernedLoopSleepStore();
        var posture = new CanonicalWaitPosturePort(store, context.Anchor.AdapterBinding.AdmissionReceipt.Intent.Publication, time);
        var nodeRelay = new GovernedLoopWaitNodeExecutionRelay();
        var continuationRelay = new GovernedLoopWaitContinuationRelay();
        var orderedResume = new BoundWaitOrderedResumePort();
        var sleep = new GovernedLoopSleepService(
            sleepStore,
            posture,
            continuationRelay,
            new StubGovernedLoopAuthenticatedWakeVerificationPort(),
            time);
        var gate = new TestExecutionGate();
        var authority = new StubCapabilityAuthorityTransaction();
        var wait = new GovernedLoopWaitExecutionService(
            store,
            sleep,
            posture,
            authority,
            gate,
            orderedResume,
            time);
        nodeRelay.Bind(wait);
        continuationRelay.Bind(wait);

        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var runtime = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(store, new QueueExecutor(Result("wait result")), timeProvider: time, waitNodeExecutor: nodeRelay),
            evidence,
            evidence);
        orderedResume.Bind(new GovernedLoopWaitOrderedContext(context.Anchor, context.Plan, context.Artifact), runtime);

        var parked = await runtime.RunAsync(new GovernedLoopSequentialOrderedRunRequest(
            GovernedLoopSequentialOrderedRunRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        Assert.Equal(CustomLoopRunStatus.Waiting, store.Current.Status);
        var waitEvidence = Assert.Single(store.Current.WaitEvidence);
        var park = Assert.IsType<GovernedLoopWaitParkEvidence>(waitEvidence.ParkEvidence);
        Assert.Equal(GovernedLoopNodeExecutionStatus.Waiting, store.Current.Frontier!.Payload.Nodes[waitEvidence.ActivationOrdinal].Status);
        Assert.Equal(1, sleepStore.CheckpointCount);

        time.UtcNow = deadline;
        var woke = await sleep.WakeAsync(new GovernedLoopWakeRequest(
            park.Checkpoint.CheckpointId,
            park.Checkpoint.ContentHash,
            null));

        Assert.Equal(GovernedLoopWakeResultStatus.Committed, woke.Status);
        Assert.True(woke.ContinuationInvoked);
        Assert.Equal(CustomLoopRunStatus.Completed, store.Current.Status);
        Assert.Equal(CustomLoopOrderedRunStatus.Completed, orderedResume.LastResult!.Status);
        var completedWait = Assert.Single(store.Current.WaitEvidence);
        var continuation = Assert.IsType<GovernedLoopWaitContinuationEvidence>(completedWait.ContinuationEvidence);
        var activation = store.Current.Frontier!.Payload.Nodes[completedWait.ActivationOrdinal];
        Assert.Equal(GovernedLoopNodeExecutionStatus.Completed, activation.Status);
        var outcome = Assert.Single(store.Current.Events, item =>
            item.Kind == CustomLoopRunEventKind.NodeAttemptCompleted
            && string.Equals(item.WaitContinuationEvidenceHash, continuation.ContentHash, StringComparison.Ordinal));
        Assert.Equal(outcome.EventId, activation.OutcomeEvidenceId);
        Assert.Equal(outcome.SequentialNodeEvidence!.OutcomeArtifactHash, activation.OutcomeEvidenceHash);
        Assert.Equal(1, orderedResume.ResumeCount);
        Assert.Equal(1, gate.AcquisitionCount);
        Assert.Equal(1, gate.ReleasedLeaseCount);
        Assert.Equal(1, authority.Executions);

        var replay = await sleep.WakeAsync(new GovernedLoopWakeRequest(
            park.Checkpoint.CheckpointId,
            park.Checkpoint.ContentHash,
            null));

        Assert.Equal(GovernedLoopWakeResultStatus.Duplicate, replay.Status);
        Assert.Equal(1, orderedResume.ResumeCount);
        Assert.Single(store.Current.Events, item =>
            item.Kind == CustomLoopRunEventKind.NodeAttemptCompleted
            && string.Equals(item.WaitContinuationEvidenceHash, continuation.ContentHash, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(WaitCrashBoundary.ParkRun)]
    [InlineData(WaitCrashBoundary.SleepCheckpoint)]
    [InlineData(WaitCrashBoundary.CheckpointAttachment)]
    public async Task Wait_parking_reconciles_each_durable_crash_boundary_without_duplicate_evidence(WaitCrashBoundary boundary)
    {
        var harness = await CreateWaitRuntimeAsync();
        var crashed = false;
        if (boundary == WaitCrashBoundary.SleepCheckpoint)
        {
            harness.SleepStore.ThrowAfterPublishCommit = true;
        }
        else
        {
            harness.Store.AfterUpdate = run =>
            {
                var wait = run.WaitEvidence.SingleOrDefault();
                var atBoundary = boundary == WaitCrashBoundary.ParkRun
                    ? wait is { ParkEvidence: null }
                    : wait?.ParkEvidence is not null;
                if (atBoundary && !crashed)
                {
                    crashed = true;
                    throw new InvalidOperationException("simulated durable Wait crash");
                }

                return Task.CompletedTask;
            };
        }

        var parked = await harness.RunToParkAsync();

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        Assert.Equal(CustomLoopRunStatus.Waiting, harness.Store.Current.Status);
        var waitEvidence = Assert.Single(harness.Store.Current.WaitEvidence);
        Assert.NotNull(waitEvidence.ParkEvidence);
        Assert.Equal(1, harness.SleepStore.CheckpointCount);
        Assert.Single(harness.Store.Current.WaitEvidence);
        Assert.Single(harness.Store.Current.Events, item => item.Kind == CustomLoopRunEventKind.LifecycleChanged
            && item.Detail.Contains("entered Waiting", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(CustomLoopExecutionLeaseStatus.WorkspaceBusy, GovernedLoopWakeResultStatus.AmbiguousAttempt)]
    [InlineData(CustomLoopExecutionLeaseStatus.WorkspaceHostUnavailable, GovernedLoopWakeResultStatus.AmbiguousAttempt)]
    [InlineData(CustomLoopExecutionLeaseStatus.OperationInProgress, GovernedLoopWakeResultStatus.AmbiguousAttempt)]
    [InlineData(CustomLoopExecutionLeaseStatus.OperationConflict, GovernedLoopWakeResultStatus.Failed)]
    public async Task Wake_requires_exact_workspace_ownership_before_continuation_cas(
        CustomLoopExecutionLeaseStatus leaseStatus,
        GovernedLoopWakeResultStatus expected)
    {
        var harness = await CreateWaitRuntimeAsync(leaseStatus);
        await harness.RunToParkAsync();

        var result = await harness.WakeAsync();

        Assert.Equal(expected, result.Status);
        Assert.Equal(CustomLoopRunStatus.Waiting, harness.Store.Current.Status);
        Assert.Null(Assert.Single(harness.Store.Current.WaitEvidence).ContinuationEvidence);
        Assert.Equal(0, harness.OrderedResume.ResumeCount);
        Assert.Equal(1, harness.Gate.AcquisitionCount);
    }

    [Theory]
    [InlineData(false, CustomLoopControlStatus.Paused, CustomLoopRunStatus.Paused, GovernedLoopNodeExecutionStatus.Waiting, GovernedLoopWakeResultStatus.Paused)]
    [InlineData(true, CustomLoopControlStatus.Cancelled, CustomLoopRunStatus.Cancelled, GovernedLoopNodeExecutionStatus.Waiting, GovernedLoopWakeResultStatus.Cancelled)]
    public async Task Lifecycle_controls_pause_or_cancel_a_durable_wait_idempotently(
        bool cancel,
        CustomLoopControlStatus expectedControl,
        CustomLoopRunStatus expectedRun,
        GovernedLoopNodeExecutionStatus expectedActivation,
        GovernedLoopWakeResultStatus expectedWake)
    {
        var harness = await CreateWaitRuntimeAsync();
        await harness.RunToParkAsync();
        var operationStore = new FakeControlOperationStore();
        var service = new CustomLoopLifecycleService(
            harness.Store,
            operationStore,
            new NoopWaitLifecycleResumeExecutor(harness.Store.Current),
            new AvailableModel(),
            new NoActiveAttemptCancellationSignal(),
            new RecordingAuditLog(),
            new TestExecutionGate(),
            new FixedTimeProvider(_now.AddSeconds(1)));
        var expectedVersion = harness.Store.Current.LifecycleVersion;
        const string OperationId = "control-durable-wait";

        CustomLoopControlResult result;
        CustomLoopControlResult replay;
        if (cancel)
        {
            var request = new CustomLoopCancelRequest(harness.Store.Current.Id, expectedVersion, OperationId, AuditSchema.Actors.Web);
            result = await service.CancelAsync(request);
            replay = await service.CancelAsync(request);
        }
        else
        {
            var request = new CustomLoopPauseRequest(harness.Store.Current.Id, expectedVersion, OperationId, AuditSchema.Actors.Web);
            result = await service.PauseAsync(request);
            replay = await service.PauseAsync(request);
        }

        Assert.Equal(expectedControl, result.Status);
        Assert.Equal(expectedControl, replay.Status);
        Assert.Equal(expectedRun, harness.Store.Current.Status);
        var wait = Assert.Single(harness.Store.Current.WaitEvidence);
        Assert.Equal(expectedActivation, harness.Store.Current.Frontier!.Payload.Nodes[wait.ActivationOrdinal].Status);
        Assert.Equal(1, harness.SleepStore.CheckpointCount);
        Assert.True(CustomLoopRunValidator.Validate(harness.Store.Current).IsValid);

        harness.Time.UtcNow = harness.Deadline;
        var park = Assert.IsType<GovernedLoopWaitParkEvidence>(wait.ParkEvidence);
        var wake = await harness.SleepService.WakeAsync(new GovernedLoopWakeRequest(
            park.Checkpoint.CheckpointId,
            park.Checkpoint.ContentHash,
            null));
        Assert.Equal(expectedWake, wake.Status);
        Assert.Null(Assert.Single(harness.Store.Current.WaitEvidence).ContinuationEvidence);
    }

    [Fact]
    public async Task Resume_rearms_a_paused_durable_wait_without_dispatch_and_replays_its_control_receipt()
    {
        var harness = await CreateWaitRuntimeAsync();
        await harness.RunToParkAsync();
        var operationStore = new FakeControlOperationStore();
        var resumeExecutor = new NoopWaitLifecycleResumeExecutor(harness.Store.Current);
        var service = new CustomLoopLifecycleService(
            harness.Store,
            operationStore,
            resumeExecutor,
            new AvailableModel(),
            new NoActiveAttemptCancellationSignal(),
            new RecordingAuditLog(),
            new TestExecutionGate(),
            new FixedTimeProvider(_now.AddSeconds(1)));

        var pause = await service.PauseAsync(new CustomLoopPauseRequest(
            harness.Store.Current.Id,
            harness.Store.Current.LifecycleVersion,
            "pause-durable-wait-before-resume",
            AuditSchema.Actors.Web));
        Assert.Equal(CustomLoopControlStatus.Paused, pause.Status);

        var resumeRequest = new CustomLoopResumeRequest(
            harness.Store.Current.Id,
            harness.Store.Current.LifecycleVersion,
            "resume-durable-wait",
            AuditSchema.Actors.Web);
        var resumed = await service.ResumeAsync(resumeRequest);
        var replayed = await service.ResumeAsync(resumeRequest);

        Assert.Equal(CustomLoopControlStatus.Waiting, resumed.Status);
        Assert.Equal(CustomLoopControlStatus.Waiting, replayed.Status);
        Assert.Equal(CustomLoopRunStatus.Waiting, harness.Store.Current.Status);
        Assert.Equal(GovernedLoopFrontierStatus.Waiting, harness.Store.Current.Frontier!.Payload.Status);
        var wait = Assert.Single(harness.Store.Current.WaitEvidence);
        Assert.Equal(GovernedLoopNodeExecutionStatus.Waiting, harness.Store.Current.Frontier.Payload.Nodes[wait.ActivationOrdinal].Status);
        Assert.NotNull(wait.ParkEvidence);
        Assert.Null(wait.ContinuationEvidence);
        Assert.Equal(0, resumeExecutor.ResumeCount);
        Assert.Equal(1, harness.SleepStore.CheckpointCount);
        Assert.True(CustomLoopRunValidator.Validate(harness.Store.Current).IsValid);

        harness.Time.UtcNow = harness.Deadline;
        var park = Assert.IsType<GovernedLoopWaitParkEvidence>(wait.ParkEvidence);
        var wake = await harness.SleepService.WakeAsync(new GovernedLoopWakeRequest(
            park.Checkpoint.CheckpointId,
            park.Checkpoint.ContentHash,
            null));
        Assert.Equal(GovernedLoopWakeResultStatus.Committed, wake.Status);
        Assert.Equal(CustomLoopRunStatus.Completed, harness.Store.Current.Status);
    }

    [Fact]
    public async Task Resume_rearms_a_paused_prepublication_wait_for_recovery_without_ordered_dispatch()
    {
        var harness = await CreateWaitRuntimeAsync();
        harness.SleepStore.ThrowBeforePublish = true;
        var parked = await harness.RunToParkAsync();
        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        Assert.Null(Assert.Single(harness.Store.Current.WaitEvidence).ParkEvidence);

        var operationStore = new FakeControlOperationStore();
        var resumeExecutor = new NoopWaitLifecycleResumeExecutor(harness.Store.Current);
        var service = new CustomLoopLifecycleService(
            harness.Store,
            operationStore,
            resumeExecutor,
            new AvailableModel(),
            new NoActiveAttemptCancellationSignal(),
            new RecordingAuditLog(),
            new TestExecutionGate(),
            new FixedTimeProvider(_now.AddSeconds(1)));
        var pause = await service.PauseAsync(new CustomLoopPauseRequest(
            harness.Store.Current.Id,
            harness.Store.Current.LifecycleVersion,
            "pause-prepublication-wait",
            AuditSchema.Actors.Web));
        Assert.Equal(CustomLoopControlStatus.Paused, pause.Status);

        var resumeRequest = new CustomLoopResumeRequest(
            harness.Store.Current.Id,
            harness.Store.Current.LifecycleVersion,
            "resume-prepublication-wait",
            AuditSchema.Actors.Web);
        var resumed = await service.ResumeAsync(resumeRequest);
        var replayed = await service.ResumeAsync(resumeRequest);

        Assert.Equal(CustomLoopControlStatus.Waiting, resumed.Status);
        Assert.Equal(CustomLoopControlStatus.Waiting, replayed.Status);
        Assert.Equal(CustomLoopRunStatus.Waiting, harness.Store.Current.Status);
        Assert.Equal(GovernedLoopFrontierStatus.Waiting, harness.Store.Current.Frontier!.Payload.Status);
        Assert.Equal(0, resumeExecutor.ResumeCount);
        Assert.Null(Assert.Single(harness.Store.Current.WaitEvidence).ParkEvidence);
        Assert.True(CustomLoopRunValidator.Validate(harness.Store.Current).IsValid);

        harness.SleepStore.ThrowBeforePublish = false;
        harness.Time.UtcNow = _now.AddSeconds(1);
        Assert.Equal(new GovernedLoopWaitRecoveryResult(1, 1, 0), await harness.WaitService.RecoverAsync(16));
        var wait = Assert.Single(harness.Store.Current.WaitEvidence);
        var park = Assert.IsType<GovernedLoopWaitParkEvidence>(wait.ParkEvidence);
        harness.Time.UtcNow = harness.Deadline;
        var wake = await harness.SleepService.WakeAsync(new GovernedLoopWakeRequest(
            park.Checkpoint.CheckpointId,
            park.Checkpoint.ContentHash,
            null));

        Assert.Equal(GovernedLoopWakeResultStatus.Committed, wake.Status);
        Assert.Equal(CustomLoopRunStatus.Completed, harness.Store.Current.Status);
    }

    [Fact]
    public async Task Wake_rechecks_fresh_posture_inside_authority_transaction_before_continuation_cas()
    {
        var harness = await CreateWaitRuntimeAsync();
        await harness.RunToParkAsync();
        harness.SleepStore.OnCreate = (_, _) => harness.Posture.PostureHash = Hash("changed-wait-posture");

        var result = await harness.WakeAsync();

        Assert.Equal(GovernedLoopWakeResultStatus.AmbiguousAttempt, result.Status);
        Assert.Equal(CustomLoopRunStatus.Waiting, harness.Store.Current.Status);
        Assert.Null(Assert.Single(harness.Store.Current.WaitEvidence).ContinuationEvidence);
        Assert.Equal(1, harness.Authority.Executions);
        Assert.Equal(0, harness.OrderedResume.ResumeCount);
    }

    [Fact]
    public async Task Recovery_publishes_a_committed_park_after_restart()
    {
        var harness = await CreateWaitRuntimeAsync();
        harness.SleepStore.ThrowBeforePublish = true;

        var parked = await harness.RunToParkAsync();

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        Assert.Null(Assert.Single(harness.Store.Current.WaitEvidence).ParkEvidence);
        Assert.Equal(0, harness.SleepStore.CheckpointCount);

        var genericRecovery = Assert.Single(await new CustomLoopRecoveryService(
            harness.Store,
            new RecordingAuditLog(),
            new FixedTimeProvider(_now.AddSeconds(1))).RecoverAsync(AuditSchema.Actors.Web));
        Assert.Equal(CustomLoopRecoveryStatus.Unchanged, genericRecovery.Status);
        Assert.Equal(CustomLoopRunStatus.Waiting, harness.Store.Current.Status);
        Assert.Null(Assert.Single(harness.Store.Current.WaitEvidence).ParkEvidence);

        harness.SleepStore.ThrowBeforePublish = false;
        var recovered = await harness.WaitService.RecoverAsync(16);

        Assert.Equal(new GovernedLoopWaitRecoveryResult(1, 1, 0), recovered);
        Assert.NotNull(Assert.Single(harness.Store.Current.WaitEvidence).ParkEvidence);
        Assert.Equal(1, harness.SleepStore.CheckpointCount);
    }

    [Fact]
    public async Task Generic_recovery_does_not_preserve_substituted_wait_evidence_as_a_restart_safe_checkpoint()
    {
        var harness = await CreateWaitRuntimeAsync();
        await harness.RunToParkAsync();
        var wait = Assert.Single(harness.Store.Current.WaitEvidence);
        var substituted = GovernedLoopWaitContractHash.Apply(wait with
        {
            WaitOperationId = "substituted-wait-operation",
            ContentHash = string.Empty,
        });
        var malformed = harness.Store.Current with { WaitEvidence = [substituted] };
        harness.Store.ReplaceCurrent(malformed, validate: false);

        var result = Assert.Single(await new CustomLoopRecoveryService(
            harness.Store,
            new RecordingAuditLog(),
            new FixedTimeProvider(_now.AddSeconds(1))).RecoverAsync(AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopRecoveryStatus.Failed, result.Status);
        Assert.Same(malformed, harness.Store.Current);
        Assert.Equal(CustomLoopRunStatus.Waiting, harness.Store.Current.Status);
        Assert.False(CustomLoopRunValidator.Validate(harness.Store.Current).IsValid);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("substituted")]
    public async Task Generic_recovery_requires_one_exact_authenticated_start_for_each_waiting_activation(string mutation)
    {
        var harness = await CreateWaitRuntimeAsync();
        await harness.RunToParkAsync();
        var current = harness.Store.Current;
        var wait = Assert.Single(current.WaitEvidence);
        var startIndex = Array.FindIndex(current.Events, item =>
            item.Kind == CustomLoopRunEventKind.NodeAttemptStarted
            && string.Equals(item.EventId, wait.WaitOperationId, StringComparison.Ordinal));
        Assert.True(startIndex >= 0);
        var start = current.Events[startIndex];
        CustomLoopRunEvent[] events;
        switch (mutation)
        {
            case "missing":
                events = current.Events
                    .Where((_, index) => index != startIndex)
                    .Select((item, index) => item with { Sequence = index + 1L })
                    .ToArray();
                break;
            case "duplicate":
                events = [.. current.Events, start];
                break;
            case "substituted":
                var substituted = start with { Detail = "Substituted Wait dispatch evidence." };
                events = current.Events.Select((item, index) => index == startIndex ? substituted : item).ToArray();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        var candidate = current with { Events = events };
        harness.Store.ReplaceCurrent(candidate, validate: false);
        var result = Assert.Single(await new CustomLoopRecoveryService(
            harness.Store,
            new RecordingAuditLog(),
            new FixedTimeProvider(_now.AddSeconds(1))).RecoverAsync(AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopRecoveryStatus.Failed, result.Status);
        Assert.Same(candidate, harness.Store.Current);
        Assert.Equal(CustomLoopRunStatus.Waiting, harness.Store.Current.Status);
    }

    [Theory]
    [InlineData(GovernedLoopSleepPublicationStatus.Cancelled, CustomLoopOrderedRunStatus.Cancelled, CustomLoopRunStatus.Cancelled, GovernedLoopFrontierStatus.Cancelled, GovernedLoopNodeExecutionStatus.Waiting)]
    [InlineData(GovernedLoopSleepPublicationStatus.Expired, CustomLoopOrderedRunStatus.Failed, CustomLoopRunStatus.Failed, GovernedLoopFrontierStatus.Failed, GovernedLoopNodeExecutionStatus.Failed)]
    [InlineData(GovernedLoopSleepPublicationStatus.ReviewBlocked, CustomLoopOrderedRunStatus.NeedsReview, CustomLoopRunStatus.NeedsReview, GovernedLoopFrontierStatus.ReviewBlocked, GovernedLoopNodeExecutionStatus.ReviewBlocked)]
    public async Task Definitive_checkpoint_publication_dispositions_close_wait_without_stranding_waiting(
        GovernedLoopSleepPublicationStatus publication,
        CustomLoopOrderedRunStatus orderedStatus,
        CustomLoopRunStatus runStatus,
        GovernedLoopFrontierStatus frontierStatus,
        GovernedLoopNodeExecutionStatus activationStatus)
    {
        var harness = await CreateWaitRuntimeAsync();
        switch (publication)
        {
            case GovernedLoopSleepPublicationStatus.Cancelled:
                harness.Posture.LifecycleStatusOverride = GovernedLoopRunStatus.CancelRequested;
                break;
            case GovernedLoopSleepPublicationStatus.Expired:
                harness.Posture.ExecutionExpiresAtUtc = harness.Time.UtcNow;
                break;
            case GovernedLoopSleepPublicationStatus.ReviewBlocked:
                harness.Posture.UnattendedExecutionPermitted = false;
                break;
        }

        var result = await harness.RunToParkAsync();

        Assert.Equal(orderedStatus, result.Status);
        Assert.Equal(runStatus, harness.Store.Current.Status);
        Assert.Equal(frontierStatus, harness.Store.Current.Frontier!.Payload.Status);
        var wait = Assert.Single(harness.Store.Current.WaitEvidence);
        Assert.Equal(activationStatus, harness.Store.Current.Frontier.Payload.Nodes[wait.ActivationOrdinal].Status);
        Assert.Null(wait.ParkEvidence);
        Assert.Null(wait.ContinuationEvidence);
        Assert.Equal(0, harness.SleepStore.CheckpointCount);
        Assert.True(CustomLoopRunValidator.Validate(harness.Store.Current).IsValid);
        Assert.Empty(harness.Store.ValidationFailures);
        Assert.Equal(new GovernedLoopWaitRecoveryResult(0, 0, 0), await harness.WaitService.RecoverAsync(16));
    }

    [Theory]
    [InlineData(GovernedLoopSleepPublicationStatus.Cancelled, CustomLoopRunStatus.Cancelled)]
    [InlineData(GovernedLoopSleepPublicationStatus.Expired, CustomLoopRunStatus.Failed)]
    [InlineData(GovernedLoopSleepPublicationStatus.ReviewBlocked, CustomLoopRunStatus.NeedsReview)]
    public async Task Definitive_publication_conflict_does_not_claim_another_terminal_writer_as_its_commit(
        GovernedLoopSleepPublicationStatus publication,
        CustomLoopRunStatus terminalStatus)
    {
        var harness = await CreateWaitRuntimeAsync();
        if (publication == GovernedLoopSleepPublicationStatus.Cancelled)
        {
            harness.Posture.LifecycleStatusOverride = GovernedLoopRunStatus.CancelRequested;
        }
        else if (publication == GovernedLoopSleepPublicationStatus.Expired)
        {
            harness.Posture.ExecutionExpiresAtUtc = harness.Time.UtcNow;
        }
        else
        {
            harness.Posture.UnattendedExecutionPermitted = false;
        }

        harness.Store.RawConflictSuccessorFactory = (current, proposed) =>
        {
            if (proposed.Status != terminalStatus || !proposed.IsTerminal)
            {
                return null;
            }

            const string ConcurrentDetail = "A concurrent controller committed the same terminal posture for a different root cause.";
            var replacementEvent = proposed.Events[^1] with
            {
                EventId = "concurrent-terminal-controller",
                Detail = ConcurrentDetail,
            };
            var successor = proposed with
            {
                Events = [.. proposed.Events[..^1], replacementEvent],
                FailureCode = terminalStatus switch
                {
                    CustomLoopRunStatus.Failed => "concurrent_failure",
                    CustomLoopRunStatus.NeedsReview => "concurrent_review",
                    _ => proposed.FailureCode,
                },
                FailureDetail = terminalStatus is CustomLoopRunStatus.Failed or CustomLoopRunStatus.NeedsReview
                    ? ConcurrentDetail
                    : proposed.FailureDetail,
            };
            var validation = CustomLoopRunValidator.ValidateUpdate(current, successor);
            Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));
            return successor;
        };

        var result = await harness.RunToParkAsync();

        Assert.Equal(terminalStatus, harness.Store.Current.Status);
        Assert.Contains("cas-failed", result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("disposition-committed", result.Detail, StringComparison.Ordinal);
        Assert.Equal("concurrent-terminal-controller", harness.Store.Current.Events[^1].EventId);
        Assert.True(CustomLoopRunValidator.Validate(harness.Store.Current).IsValid);
    }

    [Fact]
    public async Task Recovery_finishes_an_exact_wait_claim_after_process_loss_before_park_evidence()
    {
        var deadline = _now.AddMinutes(1);
        var context = await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role => TimestampWaitArtifact(role, deadline));
        var crashedStore = new FakeRunStore(context.Run);
        var time = new StubGovernedLoopSleepTimeProvider(_now);
        var evidence = new SequentialEvidenceHarness(crashedStore, context.Evidence);
        var firstRuntime = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(
                crashedStore,
                new QueueExecutor(Result("wait result")),
                timeProvider: time,
                waitNodeExecutor: new ThrowingWaitNodeExecutor()),
            evidence,
            evidence);

        var interrupted = await firstRuntime.RunAsync(new GovernedLoopSequentialOrderedRunRequest(
            GovernedLoopSequentialOrderedRunRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Failed, interrupted.Status);
        Assert.Equal(CustomLoopRunStatus.Running, crashedStore.Current.Status);
        Assert.Empty(crashedStore.Current.WaitEvidence);
        var waitActivation = Assert.Single(crashedStore.Current.Frontier!.Payload.Nodes, item =>
            item.Descriptor.Kind == GovernedLoopNodeKind.Wait
            && item.Status == GovernedLoopNodeExecutionStatus.Running);
        Assert.Single(crashedStore.Current.Events, item =>
            item.Kind == CustomLoopRunEventKind.NodeAttemptStarted
            && item.SequentialNodeEvidence?.ActivationOrdinal == waitActivation.ActivationOrdinal);

        var restartedStore = new FakeRunStore(crashedStore.Current);
        var genericRecovery = Assert.Single(await new CustomLoopRecoveryService(
            restartedStore,
            new RecordingAuditLog(),
            new FixedTimeProvider(_now.AddSeconds(1))).RecoverAsync(AuditSchema.Actors.Web));
        Assert.Equal(CustomLoopRecoveryStatus.Unchanged, genericRecovery.Status);
        Assert.Equal(CustomLoopRunStatus.Running, restartedStore.Current.Status);

        var sleepStore = new InMemoryGovernedLoopSleepStore();
        var posture = new CanonicalWaitPosturePort(restartedStore, context.Anchor.AdapterBinding.AdmissionReceipt.Intent.Publication, time);
        var continuationRelay = new GovernedLoopWaitContinuationRelay();
        var orderedResume = new BoundWaitOrderedResumePort();
        var sleep = new GovernedLoopSleepService(
            sleepStore,
            posture,
            continuationRelay,
            new StubGovernedLoopAuthenticatedWakeVerificationPort(),
            time);
        var waitService = new GovernedLoopWaitExecutionService(
            restartedStore,
            sleep,
            posture,
            new StubCapabilityAuthorityTransaction(),
            new TestExecutionGate(),
            orderedResume,
            time);
        continuationRelay.Bind(waitService);
        orderedResume.Bind(new GovernedLoopWaitOrderedContext(context.Anchor, context.Plan, context.Artifact), firstRuntime);

        Assert.Equal(new GovernedLoopWaitRecoveryResult(1, 1, 0), await waitService.RecoverAsync(16));
        Assert.Equal(CustomLoopRunStatus.Waiting, restartedStore.Current.Status);
        var recoveredWait = Assert.Single(restartedStore.Current.WaitEvidence);
        Assert.Equal(waitActivation.ActivationOrdinal, recoveredWait.ActivationOrdinal);
        Assert.NotNull(recoveredWait.ParkEvidence);
        Assert.Equal(1, sleepStore.CheckpointCount);

        Assert.Equal(new GovernedLoopWaitRecoveryResult(1, 0, 0), await waitService.RecoverAsync(16));
        Assert.Single(restartedStore.Current.WaitEvidence);
        Assert.Equal(1, sleepStore.CheckpointCount);
    }

    [Fact]
    public async Task Recovery_reenters_a_committed_continuation_without_claiming_completion_early()
    {
        var harness = await CreateWaitRuntimeAsync();
        await harness.RunToParkAsync();
        harness.OrderedResume.ThrowOnResume = true;

        var wake = await harness.WakeAsync();

        Assert.Equal(GovernedLoopWakeResultStatus.AmbiguousAttempt, wake.Status);
        Assert.Equal(CustomLoopRunStatus.Running, harness.Store.Current.Status);
        var continued = Assert.Single(harness.Store.Current.WaitEvidence);
        Assert.NotNull(continued.ContinuationEvidence);
        Assert.Equal(GovernedLoopNodeExecutionStatus.Running, harness.Store.Current.Frontier!.Payload.Nodes[continued.ActivationOrdinal].Status);

        Assert.Equal(
            new GovernedLoopWaitRecoveryResult(1, 0, 1),
            await harness.WaitService.RecoverAsync(16));

        harness.OrderedResume.ThrowOnResume = false;
        var recovered = await harness.WaitService.RecoverAsync(16);

        Assert.Equal(new GovernedLoopWaitRecoveryResult(1, 1, 0), recovered);
        Assert.Equal(CustomLoopRunStatus.Completed, harness.Store.Current.Status);
        Assert.Equal(3, harness.OrderedResume.ResumeCount);
        Assert.Single(harness.Store.Current.Events, item => item.WaitContinuationEvidenceHash is not null);
    }

    [Fact]
    public async Task Generic_restart_recovery_preserves_an_exact_committed_wait_continuation_for_wait_reconciliation()
    {
        var harness = await CreateWaitRuntimeAsync();
        await harness.RunToParkAsync();
        harness.OrderedResume.ThrowOnResume = true;

        Assert.Equal(GovernedLoopWakeResultStatus.AmbiguousAttempt, (await harness.WakeAsync()).Status);
        var interrupted = harness.Store.Current;
        var wait = Assert.Single(interrupted.WaitEvidence);
        Assert.NotNull(wait.ContinuationEvidence);
        Assert.Equal(CustomLoopRunStatus.Running, interrupted.Status);
        Assert.Equal(GovernedLoopNodeExecutionStatus.Running, interrupted.Frontier!.Payload.Nodes[wait.ActivationOrdinal].Status);

        var generic = Assert.Single(await new CustomLoopRecoveryService(
            harness.Store,
            new RecordingAuditLog(),
            new FixedTimeProvider(interrupted.UpdatedAtUtc.AddSeconds(1))).RecoverAsync(AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopRecoveryStatus.Unchanged, generic.Status);
        Assert.Same(interrupted, harness.Store.Current);
        Assert.Equal(CustomLoopRunStatus.Running, harness.Store.Current.Status);
        Assert.NotNull(Assert.Single(harness.Store.Current.WaitEvidence).ContinuationEvidence);

        harness.OrderedResume.ThrowOnResume = false;
        Assert.Equal(new GovernedLoopWaitRecoveryResult(1, 1, 0), await harness.WaitService.RecoverAsync(16));
        Assert.Equal(CustomLoopRunStatus.Completed, harness.Store.Current.Status);
    }

    [Theory]
    [InlineData(WaitCrashBoundary.ContinuationRun)]
    [InlineData(WaitCrashBoundary.CompletionRun)]
    public async Task Wake_reconciles_crash_after_continuation_or_completion_cas(WaitCrashBoundary boundary)
    {
        var harness = await CreateWaitRuntimeAsync();
        await harness.RunToParkAsync();
        var crashed = false;
        harness.Store.AfterUpdate = run =>
        {
            var wait = run.WaitEvidence.SingleOrDefault();
            var atBoundary = boundary == WaitCrashBoundary.ContinuationRun
                ? wait?.ContinuationEvidence is not null
                    && run.Frontier?.Payload.Nodes[wait.ActivationOrdinal].Status == GovernedLoopNodeExecutionStatus.Running
                : run.Events.Any(item => item.WaitContinuationEvidenceHash is not null);
            if (atBoundary && !crashed)
            {
                crashed = true;
                throw new InvalidOperationException("simulated durable continuation crash");
            }

            return Task.CompletedTask;
        };

        var wake = await harness.WakeAsync();

        Assert.Equal(GovernedLoopWakeResultStatus.Committed, wake.Status);
        var wait = Assert.Single(harness.Store.Current.WaitEvidence);
        Assert.Equal(GovernedLoopNodeExecutionStatus.Completed, harness.Store.Current.Frontier!.Payload.Nodes[wait.ActivationOrdinal].Status);
        if (harness.Store.Current.Status == CustomLoopRunStatus.Running)
        {
            var retainedContinuation = Assert.IsType<GovernedLoopWaitContinuationEvidence>(wait.ContinuationEvidence);
            var resumed = await harness.OrderedResume.ResumeAsync(new GovernedLoopWaitOrderedResumeRequest(
                new GovernedLoopWaitOrderedContext(harness.Context.Anchor, harness.Context.Plan, harness.Context.Artifact),
                wait.ActivationOrdinal,
                retainedContinuation.ContentHash,
                AuditSchema.Actors.Web));
            Assert.Equal(CustomLoopOrderedRunStatus.Completed, resumed.Status);
        }

        Assert.Equal(CustomLoopRunStatus.Completed, harness.Store.Current.Status);
        Assert.Single(harness.Store.Current.WaitEvidence);
        var terminal = Assert.Single(harness.Store.Current.Events, item => item.WaitContinuationEvidenceHash is not null);
        var outcomeOperationId = GovernedLoopSequentialAuditOperationId.ForNodeOutcome(terminal.SequentialNodeEvidence!.EvidenceHash);
        Assert.Equal(1, harness.Evidence.DurableAuditCount(outcomeOperationId));

        var continuation = Assert.IsType<GovernedLoopWaitContinuationEvidence>(wait.ContinuationEvidence);
        var replayed = await harness.OrderedResume.ResumeAsync(new GovernedLoopWaitOrderedResumeRequest(
            new GovernedLoopWaitOrderedContext(harness.Context.Anchor, harness.Context.Plan, harness.Context.Artifact),
            wait.ActivationOrdinal,
            continuation.ContentHash,
            AuditSchema.Actors.Web));
        Assert.Equal(CustomLoopOrderedRunStatus.Completed, replayed.Status);
        Assert.Equal(1, harness.Evidence.DurableAuditCount(outcomeOperationId));
    }

    [Fact]
    public async Task Wait_service_fails_closed_for_invalid_dispatch_and_invalid_continuation_shapes()
    {
        var harness = await CreateWaitRuntimeAsync();

        var invalidPark = await harness.WaitService.ParkAsync(null!);
        var invalidContinuation = await harness.WaitService.ContinueAsync(null!);
        var invalidRecoveryLow = await harness.WaitService.RecoverAsync(0);
        var invalidRecoveryHigh = await harness.WaitService.RecoverAsync(257);

        Assert.Equal(GovernedLoopWaitParkResultStatus.Invalid, invalidPark.Status);
        Assert.Equal(GovernedLoopWakeContinuationStatus.Conflict, invalidContinuation!.Status);
        Assert.Equal(new GovernedLoopWaitRecoveryResult(0, 0, 0), invalidRecoveryLow);
        Assert.Equal(new GovernedLoopWaitRecoveryResult(0, 0, 0), invalidRecoveryHigh);
    }

    [Fact]
    public async Task Park_replays_the_exact_canonical_checkpoint_without_duplicate_writes()
    {
        var harness = await CreateWaitRuntimeAsync();
        await harness.RunToParkAsync();
        var writes = harness.Store.Writes.Count;

        var replay = await harness.WaitService.ParkAsync(harness.ParkRequest!);

        Assert.Equal(GovernedLoopWaitParkResultStatus.Replayed, replay.Status);
        Assert.Equal(writes, harness.Store.Writes.Count);
        Assert.Equal(1, harness.SleepStore.CheckpointCount);
        Assert.Single(harness.Store.Current.WaitEvidence);
    }

    [Fact]
    public async Task Wait_resume_executor_reconstructs_immutable_context_and_forwards_only_the_exact_resume_request()
    {
        var context = await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role => TimestampWaitArtifact(role, _now.AddMinutes(1)));
        var store = new FakeRunStore(context.Run);
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var runtime = new RecordingSequentialOrderedRuntime(context.Run);
        var executor = new GovernedLoopSequentialWaitResumeExecutor(evidence, context.Store, context.Store, runtime);

        var resolved = await executor.ResolveAsync(context.Run);
        var result = await executor.ResumeAsync(new GovernedLoopWaitOrderedResumeRequest(
            Assert.IsType<GovernedLoopWaitOrderedContext>(resolved),
            2,
            Hash("exact-wait-continuation"),
            AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Completed, result.Status);
        var forwarded = Assert.IsType<GovernedLoopSequentialOrderedWaitResumeRequest>(runtime.LastWaitRequest);
        Assert.Equal(context.Anchor.AdapterBinding.ContentHash, forwarded.Anchor.AdapterBinding.ContentHash);
        Assert.Equal(context.Anchor.InvocationSnapshot.ContentHash, forwarded.Anchor.InvocationSnapshot.ContentHash);
        Assert.Equal(context.Plan.GraphArtifactHash, forwarded.Plan.GraphArtifactHash);
        Assert.Equal(context.Plan.GraphLayoutHash, forwarded.Plan.GraphLayoutHash);
        Assert.Equal(context.Plan.Nodes.Count, forwarded.Plan.Nodes.Count);
        Assert.Equal(context.Artifact.ArtifactHash, forwarded.Artifact.ArtifactHash);
        Assert.Equal(context.Artifact.LayoutHash, forwarded.Artifact.LayoutHash);
        Assert.Equal(2, forwarded.ActivationOrdinal);
        Assert.Equal(Hash("exact-wait-continuation"), forwarded.ContinuationEvidenceHash);
        Assert.Equal(AuditSchema.Actors.Web, forwarded.Actor);
    }

    [Fact]
    public async Task Wait_resume_executor_fails_closed_when_immutable_admission_or_graph_evidence_is_missing()
    {
        var context = await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role => TimestampWaitArtifact(role, _now.AddMinutes(1)));
        var store = new FakeRunStore(context.Run);
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var runtime = new RecordingSequentialOrderedRuntime(context.Run);
        var executor = new GovernedLoopSequentialWaitResumeExecutor(evidence, context.Store, context.Store, runtime);
        var admittedRead = context.Store.StoreReadResult;

        context.Store.StoreReadResult = new GovernedLoopAdmissionStoreReadResult(
            GovernedLoopAdmissionStoreReadStatus.NotFound,
            3,
            null);
        Assert.Null(await executor.ResolveAsync(context.Run));

        context.Store.StoreReadResult = admittedRead;
        context.Store.GraphReadResult = new GovernedLoopGraphRevisionArtifactReadResult(
            GovernedLoopRevisionStoreReadStatus.NotFound,
            3,
            null);
        Assert.Null(await executor.ResolveAsync(context.Run));
        Assert.Null(await executor.ResolveAsync(context.Run with { SequentialAdapterBinding = null }));
        Assert.Null(runtime.LastWaitRequest);
    }

    [Fact]
    public async Task Wait_resume_executor_propagates_cancellation_and_rejects_substituted_or_unreadable_evidence()
    {
        var context = await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role => TimestampWaitArtifact(role, _now.AddMinutes(1)));
        var store = new FakeRunStore(context.Run);
        var runtime = new RecordingSequentialOrderedRuntime(context.Run);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var normalEvidence = new StaticSequentialRunEvidenceSource(context.Evidence);
        var executor = new GovernedLoopSequentialWaitResumeExecutor(normalEvidence, context.Store, context.Store, runtime);
        await Assert.ThrowsAsync<OperationCanceledException>(() => executor.ResolveAsync(context.Run, cancelled.Token));

        var substitutedEvidence = context.Evidence with
        {
            InvocationSnapshot = context.Evidence.InvocationSnapshot with { ContentHash = Hash("substituted-invocation") },
        };
        Assert.Null(await new GovernedLoopSequentialWaitResumeExecutor(
            new StaticSequentialRunEvidenceSource(substitutedEvidence),
            context.Store,
            context.Store,
            runtime).ResolveAsync(context.Run));

        context.Store.AfterStoreRead = _ => throw new InvalidOperationException("simulated admission read failure");
        Assert.Null(await executor.ResolveAsync(context.Run));
        context.Store.AfterStoreRead = null;

        context.Store.AfterMutableRead = kind =>
        {
            if (string.Equals(kind, "graph", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("simulated graph read failure");
            }
        };
        Assert.Null(await executor.ResolveAsync(context.Run));
        context.Store.AfterMutableRead = null;

        Assert.Null(await executor.ResolveAsync(context.Run with { Frontier = null }));
    }

    [Fact]
    public async Task Park_fails_closed_for_canonical_read_clock_and_cas_failures()
    {
        var harness = await CreateWaitRuntimeAsync();
        await harness.RunToParkAsync();
        var request = Assert.IsType<GovernedLoopSequentialNodeDispatchRequest>(harness.ParkRequest);
        var claimed = Assert.Single(harness.Store.Writes, run =>
            run.WaitEvidence.Count == 0
            && run.Frontier!.Payload.Nodes.Any(item => item.Descriptor.Kind == GovernedLoopNodeKind.Wait
                && item.Status == GovernedLoopNodeExecutionStatus.Running));
        harness.Store.ReplaceCurrent(claimed);
        harness.Store.GetException = new InvalidOperationException("simulated canonical read failure");
        Assert.Equal(GovernedLoopWaitParkResultStatus.Unavailable, (await harness.WaitService.ParkAsync(request)).Status);

        harness.Store.GetException = null;
        harness.Store.ReadSubstitutionFactory = current => current with { SequentialAdapterBinding = null };
        Assert.Equal(GovernedLoopWaitParkResultStatus.Conflict, (await harness.WaitService.ParkAsync(request)).Status);

        harness.Store.ReadSubstitutionFactory = null;
        harness.Time.Exception = new InvalidOperationException("simulated trusted-clock failure");
        Assert.Equal(GovernedLoopWaitParkResultStatus.Unavailable, (await harness.WaitService.ParkAsync(request)).Status);

        harness.Time.Exception = null;
        harness.Store.RawConflictSuccessorFactory = (current, _) => current;
        Assert.Equal(GovernedLoopWaitParkResultStatus.Conflict, (await harness.WaitService.ParkAsync(request)).Status);
        Assert.Empty(harness.Store.Current.WaitEvidence);
    }

    [Fact]
    public async Task Recovery_rejects_unavailable_null_duplicate_and_unreadable_candidate_sets()
    {
        var harness = await CreateWaitRuntimeAsync();
        await harness.RunToParkAsync();

        harness.Store.ListNonterminalException = new InvalidOperationException("simulated enumeration failure");
        Assert.Equal(new GovernedLoopWaitRecoveryResult(0, 0, 1), await harness.WaitService.RecoverAsync(16));

        harness.Store.ListNonterminalException = null;
        harness.Store.ReturnNullNonterminalList = true;
        Assert.Equal(new GovernedLoopWaitRecoveryResult(0, 0, 1), await harness.WaitService.RecoverAsync(16));

        harness.Store.ReturnNullNonterminalList = false;
        harness.Store.ReturnDuplicateNonterminalList = true;
        Assert.Equal(new GovernedLoopWaitRecoveryResult(0, 0, 1), await harness.WaitService.RecoverAsync(16));

        harness.Store.ReturnDuplicateNonterminalList = false;
        harness.Store.GetException = new InvalidOperationException("simulated candidate read failure");
        Assert.Equal(new GovernedLoopWaitRecoveryResult(1, 0, 1), await harness.WaitService.RecoverAsync(16));
    }

    [Fact]
    public async Task Wait_recovery_ignores_a_valid_unrelated_legacy_nonterminal_run()
    {
        var harness = await CreateWaitRuntimeAsync();
        var legacy = Run(SequentialDefinition());
        Assert.Null(legacy.SequentialAdapterBinding);
        Assert.Null(legacy.Frontier);
        Assert.Empty(legacy.WaitEvidence);
        harness.Store.ReplaceCurrent(legacy);

        Assert.Equal(new GovernedLoopWaitRecoveryResult(0, 0, 0), await harness.WaitService.RecoverAsync(16));
        Assert.Same(legacy, harness.Store.Current);
    }

    [Fact]
    public async Task Wake_reports_gate_authority_posture_and_context_unavailability_without_mutating_the_frontier()
    {
        var gateFailure = await CreateWaitRuntimeAsync();
        await gateFailure.RunToParkAsync();
        gateFailure.Gate.AcquisitionException = new InvalidOperationException("simulated gate failure");
        Assert.Equal(GovernedLoopWakeResultStatus.AmbiguousAttempt, (await gateFailure.WakeAsync()).Status);
        Assert.Null(Assert.Single(gateFailure.Store.Current.WaitEvidence).ContinuationEvidence);

        var authorityFailure = await CreateWaitRuntimeAsync();
        await authorityFailure.RunToParkAsync();
        authorityFailure.Authority.Exception = new InvalidOperationException("simulated authority failure");
        Assert.Equal(GovernedLoopWakeResultStatus.AmbiguousAttempt, (await authorityFailure.WakeAsync()).Status);
        Assert.Null(Assert.Single(authorityFailure.Store.Current.WaitEvidence).ContinuationEvidence);
        Assert.Equal(1, authorityFailure.Gate.ReleasedLeaseCount);

        var postureFailure = await CreateWaitRuntimeAsync();
        await postureFailure.RunToParkAsync();
        postureFailure.SleepStore.OnCreate = (_, _) => postureFailure.Posture.Exception = new InvalidOperationException("simulated fresh posture failure");
        Assert.Equal(GovernedLoopWakeResultStatus.AmbiguousAttempt, (await postureFailure.WakeAsync()).Status);
        Assert.Null(Assert.Single(postureFailure.Store.Current.WaitEvidence).ContinuationEvidence);

        var clockFailure = await CreateWaitRuntimeAsync();
        await clockFailure.RunToParkAsync();
        clockFailure.SleepStore.OnCreate = (_, _) => clockFailure.Time.ThrowOnCall = clockFailure.Time.CallCount + 1;
        Assert.Equal(GovernedLoopWakeResultStatus.Unavailable, (await clockFailure.WakeAsync()).Status);
        Assert.Null(Assert.Single(clockFailure.Store.Current.WaitEvidence).ContinuationEvidence);

        var ineligible = await CreateWaitRuntimeAsync();
        await ineligible.RunToParkAsync();
        ineligible.SleepStore.OnCreate = (_, _) => ineligible.Posture.UnattendedExecutionPermitted = false;
        Assert.Equal(GovernedLoopWakeResultStatus.AmbiguousAttempt, (await ineligible.WakeAsync()).Status);
        Assert.Null(Assert.Single(ineligible.Store.Current.WaitEvidence).ContinuationEvidence);

        var contextFailure = await CreateWaitRuntimeAsync();
        await contextFailure.RunToParkAsync();
        contextFailure.SleepStore.OnCreate = (_, _) => contextFailure.OrderedResume.ReturnMissingContext = true;
        Assert.Equal(GovernedLoopWakeResultStatus.Failed, (await contextFailure.WakeAsync()).Status);
        Assert.Null(Assert.Single(contextFailure.Store.Current.WaitEvidence).ContinuationEvidence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Wake_attaches_a_published_checkpoint_after_attachment_conflict_before_continuing(bool retainConflict)
    {
        var harness = await CreateWaitRuntimeAsync();
        harness.Store.RawConflictSuccessorFactory = (current, candidate) =>
            candidate.WaitEvidence.SingleOrDefault()?.ParkEvidence is not null ? current : null;

        var parked = await harness.RunToParkAsync();

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        Assert.Null(Assert.Single(harness.Store.Current.WaitEvidence).ParkEvidence);
        var checkpoint = Assert.IsType<GovernedLoopSleepCheckpoint>(harness.SleepStore.SingleCheckpoint);
        if (!retainConflict)
        {
            harness.Store.RawConflictSuccessorFactory = null;
        }

        harness.Time.UtcNow = harness.Deadline;
        var wake = await harness.SleepService.WakeAsync(new GovernedLoopWakeRequest(
            checkpoint.CheckpointId,
            checkpoint.ContentHash,
            null));

        if (retainConflict)
        {
            Assert.Equal(GovernedLoopWakeResultStatus.Failed, wake.Status);
            Assert.Null(Assert.Single(harness.Store.Current.WaitEvidence).ParkEvidence);
            Assert.Equal(CustomLoopRunStatus.Waiting, harness.Store.Current.Status);
        }
        else
        {
            Assert.Equal(GovernedLoopWakeResultStatus.Committed, wake.Status);
            Assert.NotNull(Assert.Single(harness.Store.Current.WaitEvidence).ParkEvidence);
            Assert.Equal(CustomLoopRunStatus.Completed, harness.Store.Current.Status);
        }
    }

    [Fact]
    public async Task Direct_reconciliation_distinguishes_missing_committed_and_unavailable_continuation_evidence()
    {
        var harness = await CreateWaitRuntimeAsync(CustomLoopExecutionLeaseStatus.WorkspaceBusy);
        await harness.RunToParkAsync();
        var ambiguous = await harness.WakeAsync();
        var park = Assert.IsType<GovernedLoopWaitParkEvidence>(Assert.Single(harness.Store.Current.WaitEvidence).ParkEvidence);
        var wakeEvidence = Assert.IsType<GovernedLoopWakeEvidence>(ambiguous.Evidence);
        var wakeRead = await harness.SleepStore.ReadWakeAsync(wakeEvidence.Identity.WakeId);
        var prepared = Assert.IsType<GovernedLoopWakeEvidence>(wakeRead!.PreparedEvidence);
        var reconciliationRequest = new GovernedLoopWakeContinuationRequest(
            park.Checkpoint,
            prepared.Identity,
            prepared.ContinuationOperationId!,
            null,
            null);

        var missing = await harness.WaitService.ReconcileAsync(reconciliationRequest);

        Assert.Equal(GovernedLoopWakeContinuationStatus.NotCommitted, missing!.Status);

        harness.Gate.Status = CustomLoopExecutionLeaseStatus.Acquired;
        harness.Store.GetException = new InvalidOperationException("simulated continuation read failure");
        var unavailable = await harness.WaitService.ReconcileAsync(reconciliationRequest);
        Assert.Equal(GovernedLoopWakeContinuationStatus.Unavailable, unavailable!.Status);

        harness.Store.GetException = null;
        var reconciled = await harness.SleepService.ReconcileAsync(new GovernedLoopWakeReconciliationRequest(
            park.Checkpoint.CheckpointId,
            prepared.Identity.WakeId));
        Assert.Equal(GovernedLoopWakeResultStatus.Committed, reconciled.Status);
        Assert.Equal(CustomLoopRunStatus.Completed, harness.Store.Current.Status);

        var committed = await harness.WaitService.ReconcileAsync(reconciliationRequest);
        Assert.Equal(GovernedLoopWakeContinuationStatus.Committed, committed!.Status);
    }

    [Fact]
    public async Task Ordered_runtime_requires_the_canonical_wait_executor_before_parking()
    {
        var context = await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role => TimestampWaitArtifact(role, _now.AddMinutes(1)));
        var store = new FakeRunStore(context.Run);
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var runtime = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(store, new QueueExecutor(Result("wait result"))),
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
        Assert.Equal("canonical_wait_executor_unavailable", store.Current.FailureCode);
        Assert.Empty(store.Current.WaitEvidence);
    }

    [Fact]
    public async Task Canonical_wait_at_the_run_deadline_closes_its_retained_claim_without_a_duplicate_start()
    {
        var context = await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role => TimestampWaitArtifact(role, _now.AddMinutes(1)));
        var store = new FakeRunStore(context.Run);
        var time = new CanonicalWaitDeadlineTimeProvider(_now, store);
        var waitExecutor = new CountingWaitNodeExecutor();
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var runtime = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(
                store,
                new QueueExecutor(Result("inference completed before the deadline")),
                timeProvider: time,
                waitNodeExecutor: waitExecutor),
            evidence,
            evidence);

        var result = await runtime.RunAsync(new GovernedLoopSequentialOrderedRunRequest(
            GovernedLoopSequentialOrderedRunRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Failed, result.Status);
        Assert.Equal(CustomLoopRunStatus.Failed, store.Current.Status);
        Assert.Equal("run_deadline_exceeded", store.Current.FailureCode);
        Assert.Equal(0, waitExecutor.ParkCount);
        var activation = Assert.Single(store.Current.Frontier!.Payload.Nodes, item => item.Descriptor.Kind == GovernedLoopNodeKind.Wait);
        Assert.Equal(GovernedLoopNodeExecutionStatus.Failed, activation.Status);
        var waitEvents = store.Current.Events.Where(item =>
            item.SequentialNodeEvidence?.ActivationOrdinal == activation.ActivationOrdinal).ToArray();
        var started = Assert.Single(waitEvents, item => item.Kind == CustomLoopRunEventKind.NodeAttemptStarted);
        var failed = Assert.Single(waitEvents, item => item.Kind == CustomLoopRunEventKind.NodeAttemptFailed);
        Assert.NotEqual(started.EventId, failed.EventId);
        Assert.Equal(CustomLoopSequentialNodeEvidenceKind.DispatchStarted, started.SequentialNodeEvidence!.Kind);
        Assert.Equal(CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection, failed.SequentialNodeEvidence!.Kind);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
        Assert.Empty(store.ValidationFailures);
    }

    [Fact]
    public async Task Ordered_wait_resume_rejects_substituted_evidence_then_replays_exact_completion()
    {
        var harness = await CreateWaitRuntimeAsync();
        await harness.RunToParkAsync();
        harness.OrderedResume.ThrowOnResume = true;
        Assert.Equal(GovernedLoopWakeResultStatus.AmbiguousAttempt, (await harness.WakeAsync()).Status);
        var wait = Assert.Single(harness.Store.Current.WaitEvidence);
        var continuation = Assert.IsType<GovernedLoopWaitContinuationEvidence>(wait.ContinuationEvidence);

        var substituted = await harness.Runtime.ResumeWaitAsync(new GovernedLoopSequentialOrderedWaitResumeRequest(
            GovernedLoopSequentialOrderedWaitResumeRequest.CurrentSchemaVersion,
            harness.Context.Anchor,
            harness.Context.Plan,
            harness.Context.Artifact,
            wait.ActivationOrdinal,
            Hash("substituted-continuation"),
            AuditSchema.Actors.Web));
        Assert.Equal(CustomLoopOrderedRunStatus.InvalidState, substituted.Status);

        harness.OrderedResume.ThrowOnResume = false;
        var exactRequest = new GovernedLoopSequentialOrderedWaitResumeRequest(
            GovernedLoopSequentialOrderedWaitResumeRequest.CurrentSchemaVersion,
            harness.Context.Anchor,
            harness.Context.Plan,
            harness.Context.Artifact,
            wait.ActivationOrdinal,
            continuation.ContentHash,
            AuditSchema.Actors.Web);
        var completed = await harness.Runtime.ResumeWaitAsync(exactRequest);
        var replayed = await harness.Runtime.ResumeWaitAsync(exactRequest);

        Assert.Equal(CustomLoopOrderedRunStatus.Completed, completed.Status);
        Assert.Equal(CustomLoopOrderedRunStatus.Completed, replayed.Status);
        Assert.Equal(CustomLoopRunStatus.Completed, harness.Store.Current.Status);
        Assert.Single(harness.Store.Current.Events, item => item.WaitContinuationEvidenceHash is not null);
    }

    private static async Task<WaitRuntimeHarness> CreateWaitRuntimeAsync(
        CustomLoopExecutionLeaseStatus leaseStatus = CustomLoopExecutionLeaseStatus.Acquired)
    {
        var deadline = _now.AddMinutes(1);
        var context = await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role => TimestampWaitArtifact(role, deadline));
        var store = new FakeRunStore(context.Run);
        var time = new StubGovernedLoopSleepTimeProvider(_now);
        var sleepStore = new InMemoryGovernedLoopSleepStore();
        var posture = new CanonicalWaitPosturePort(store, context.Anchor.AdapterBinding.AdmissionReceipt.Intent.Publication, time);
        var nodeRelay = new GovernedLoopWaitNodeExecutionRelay();
        var continuationRelay = new GovernedLoopWaitContinuationRelay();
        var orderedResume = new BoundWaitOrderedResumePort();
        var sleep = new GovernedLoopSleepService(
            sleepStore,
            posture,
            continuationRelay,
            new StubGovernedLoopAuthenticatedWakeVerificationPort(),
            time);
        var gate = new TestExecutionGate(leaseStatus);
        var authority = new StubCapabilityAuthorityTransaction();
        var wait = new GovernedLoopWaitExecutionService(store, sleep, posture, authority, gate, orderedResume, time);
        var recordingWait = new RecordingWaitNodeExecutor(wait);
        nodeRelay.Bind(recordingWait);
        continuationRelay.Bind(wait);
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var runtime = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(store, new QueueExecutor(Result("wait result")), timeProvider: time, waitNodeExecutor: nodeRelay),
            evidence,
            evidence);
        orderedResume.Bind(new GovernedLoopWaitOrderedContext(context.Anchor, context.Plan, context.Artifact), runtime);
        return new WaitRuntimeHarness(
            context,
            store,
            time,
            sleepStore,
            posture,
            gate,
            authority,
            orderedResume,
            wait,
            sleep,
            runtime,
            recordingWait,
            evidence,
            deadline);
    }

    private static GovernedLoopGraphRevisionArtifact TimestampWaitArtifact(
        ContextualRoleRevisionPin owningRole,
        DateTimeOffset deadline)
    {
        var trigger = GovernedLoopSequentialApplicationTestFixture.Trigger("trigger");
        var inference = GovernedLoopSequentialApplicationTestFixture.Inference("infer-01", "Produce the result before waiting.");
        var wait = new GovernedLoopNodeDefinition(
            "wait",
            GovernedLoopSequentialNodeDescriptors.TimestampWait,
            [],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [GovernedLoopWaitVocabulary.DeadlineUtcParameter] = deadline.ToString(
                    GovernedLoopWaitVocabulary.CanonicalUtcTimestampFormat,
                    System.Globalization.CultureInfo.InvariantCulture),
            });
        var exit = GovernedLoopSequentialApplicationTestFixture.Exit("exit");
        return GovernedLoopSequentialApplicationTestFixture.Artifact(
            [trigger, inference, wait, exit],
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-inference", "trigger", "infer-01", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("inference-to-wait", "infer-01", "wait", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("wait-to-exit", "wait", "exit", GovernedLoopControlCondition.Success),
            ],
            ["exit"],
            owningRole,
            bindings:
            [
                new GovernedLoopBindingDefinition("request-to-inference", GovernedLoopBindingKind.Data, "trigger", "request", "infer-01", "request"),
                new GovernedLoopBindingDefinition("context-to-inference", GovernedLoopBindingKind.Context, "trigger", "invocation-context", "infer-01", "invocation-context"),
                new GovernedLoopBindingDefinition("result-to-exit", GovernedLoopBindingKind.Data, "infer-01", "result", "exit", "result"),
            ],
            authorityCeiling: GovernedLoopAuthorityCeiling.Create(
                [
                    GovernedLoopSequentialApplicationTestFixture.ConversationTurnCapabilityId,
                    GovernedLoopSequentialApplicationTestFixture.ModelInferenceCapabilityId,
                ]));
    }

    private sealed class BoundWaitOrderedResumePort : IGovernedLoopWaitOrderedResumePort
    {
        private GovernedLoopWaitOrderedContext? _context;
        private IGovernedLoopSequentialOrderedRuntime? _runtime;

        internal int ResumeCount { get; private set; }

        internal CustomLoopOrderedRunResult? LastResult { get; private set; }

        internal bool ThrowOnResume { get; set; }

        internal bool ReturnMissingContext { get; set; }

        internal int ResolveCount { get; private set; }

        internal void Bind(GovernedLoopWaitOrderedContext context, IGovernedLoopSequentialOrderedRuntime runtime)
        {
            _context = context;
            _runtime = runtime;
        }

        public Task<GovernedLoopWaitOrderedContext?> ResolveAsync(
            EmbodySense.Core.Common.Loops.Custom.Execution.CustomLoopRunRecord run,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResolveCount++;
            return Task.FromResult<GovernedLoopWaitOrderedContext?>(
                !ReturnMissingContext
                && _context is not null
                && string.Equals(_context.Anchor.AdapterBinding.ExecutionBinding.RunId, run.Id, StringComparison.Ordinal)
                    ? _context
                    : null);
        }

        public async Task<CustomLoopOrderedRunResult> ResumeAsync(
            GovernedLoopWaitOrderedResumeRequest request,
            CancellationToken cancellationToken = default)
        {
            ResumeCount++;
            if (ThrowOnResume)
            {
                throw new InvalidOperationException("simulated ordered Wait resume failure");
            }

            LastResult = await _runtime!.ResumeWaitAsync(new GovernedLoopSequentialOrderedWaitResumeRequest(
                GovernedLoopSequentialOrderedWaitResumeRequest.CurrentSchemaVersion,
                request.Context.Anchor,
                request.Context.Plan,
                request.Context.Artifact,
                request.ActivationOrdinal,
                request.ContinuationEvidenceHash,
                request.Actor), cancellationToken);
            return LastResult;
        }
    }

    private sealed class CanonicalWaitPosturePort(
        FakeRunStore store,
        EmbodySense.Core.Common.Loops.Revisions.Models.GovernedLoopRevisionPublicationPin publication,
        TimeProvider timeProvider) : IGovernedLoopSleepCurrentPosturePort
    {
        internal GovernedLoopSleepCurrentPostureReadResult? Override { get; set; }

        internal Exception? Exception { get; set; }

        internal string PostureHash { get; set; } = Hash("wait-current-posture");

        internal bool UnattendedExecutionPermitted { get; set; } = true;

        internal GovernedLoopRunStatus? LifecycleStatusOverride { get; set; }

        internal DateTimeOffset? ExecutionExpiresAtUtc { get; set; }

        internal int ReadCount { get; private set; }

        public Task<GovernedLoopSleepCurrentPostureReadResult?> ReadAsync(
            GovernedLoopExecutionBinding binding,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            if (Exception is not null)
            {
                throw Exception;
            }

            if (Override is not null)
            {
                return Task.FromResult<GovernedLoopSleepCurrentPostureReadResult?>(Override);
            }

            var run = store.Current;
            if (run.SequentialAdapterBinding is null
                || run.Frontier is null
                || !Equals(run.SequentialAdapterBinding.ExecutionBinding, binding))
            {
                return Task.FromResult<GovernedLoopSleepCurrentPostureReadResult?>(
                    new GovernedLoopSleepCurrentPostureReadResult(GovernedLoopSleepCurrentPostureReadStatus.NotFound));
            }

            var lifecycleStatus = LifecycleStatusOverride ?? run.Status switch
            {
                CustomLoopRunStatus.Admitted => GovernedLoopRunStatus.Admitted,
                CustomLoopRunStatus.Running => GovernedLoopRunStatus.Running,
                CustomLoopRunStatus.Waiting => GovernedLoopRunStatus.Waiting,
                CustomLoopRunStatus.PauseRequested => GovernedLoopRunStatus.PauseRequested,
                CustomLoopRunStatus.Paused => GovernedLoopRunStatus.Paused,
                CustomLoopRunStatus.CancelRequested => GovernedLoopRunStatus.CancelRequested,
                CustomLoopRunStatus.Completed => GovernedLoopRunStatus.Completed,
                CustomLoopRunStatus.Failed => GovernedLoopRunStatus.Failed,
                CustomLoopRunStatus.Cancelled => GovernedLoopRunStatus.Cancelled,
                CustomLoopRunStatus.NeedsReview => GovernedLoopRunStatus.NeedsReview,
                _ => GovernedLoopRunStatus.Unknown,
            };
            var lifecycle = GovernedLoopRunLifecycle.Create(
                binding,
                GovernedLoopRunLifecyclePayload.Create(
                    1,
                    run.LifecycleVersion,
                    lifecycleStatus,
                    run.CreatedAtUtc,
                    run.UpdatedAtUtc,
                    run.IsTerminal ? run.UpdatedAtUtc : null));
            var posture = new GovernedLoopSleepCurrentPosture(
                GovernedLoopExecutionEvidenceSet.Create(1, lifecycle, run.Frontier, [], []),
                publication,
                UnattendedExecutionPermitted,
                Hash("wait-unattended-authority"),
                ExecutionExpiresAtUtc,
                timeProvider.GetUtcNow(),
                PostureHash);
            return Task.FromResult<GovernedLoopSleepCurrentPostureReadResult?>(
                new GovernedLoopSleepCurrentPostureReadResult(GovernedLoopSleepCurrentPostureReadStatus.Found, posture));
        }
    }

    private sealed class RecordingWaitNodeExecutor(IGovernedLoopWaitNodeExecutor target) : IGovernedLoopWaitNodeExecutor
    {
        internal GovernedLoopSequentialNodeDispatchRequest? LastRequest { get; private set; }

        public Task<GovernedLoopWaitParkResult> ParkAsync(
            GovernedLoopSequentialNodeDispatchRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return target.ParkAsync(request, cancellationToken);
        }
    }

    private sealed class ThrowingWaitNodeExecutor : IGovernedLoopWaitNodeExecutor
    {
        public Task<GovernedLoopWaitParkResult> ParkAsync(
            GovernedLoopSequentialNodeDispatchRequest request,
            CancellationToken cancellationToken = default)
            => throw new IOException("simulated process loss after the Wait claim CAS");
    }

    private sealed class CountingWaitNodeExecutor : IGovernedLoopWaitNodeExecutor
    {
        internal int ParkCount { get; private set; }

        public Task<GovernedLoopWaitParkResult> ParkAsync(
            GovernedLoopSequentialNodeDispatchRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ParkCount++;
            return Task.FromResult(new GovernedLoopWaitParkResult(
                GovernedLoopWaitParkResultStatus.Invalid,
                null,
                null,
                "unexpected Wait dispatch"));
        }
    }

    private sealed class NoopWaitLifecycleResumeExecutor(CustomLoopRunRecord run) : ICustomLoopResumeExecutor
    {
        internal int ResumeCount { get; private set; }

        public Task<CustomLoopOrderedRunResult> ResumeAsync(
            CustomLoopResumeExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResumeCount++;
            return Task.FromResult(new CustomLoopOrderedRunResult(CustomLoopOrderedRunStatus.Waiting, run, "test Wait resume"));
        }
    }

    private sealed class CanonicalWaitDeadlineTimeProvider(DateTimeOffset now, FakeRunStore store) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
            => store.Current.Events.Any(item =>
                item.Kind == CustomLoopRunEventKind.NodeAttemptCompleted
                && string.Equals(item.StepId, "infer-01", StringComparison.Ordinal))
                ? now.AddMilliseconds(CustomLoopLimits.MaxRunExecutionMilliseconds)
                : now;
    }

    private sealed class RecordingSequentialOrderedRuntime(CustomLoopRunRecord run) : IGovernedLoopSequentialOrderedRuntime
    {
        internal GovernedLoopSequentialOrderedWaitResumeRequest? LastWaitRequest { get; private set; }

        public Task<CustomLoopOrderedRunResult> RunAsync(
            GovernedLoopSequentialOrderedRunRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new CustomLoopOrderedRunResult(CustomLoopOrderedRunStatus.Completed, run, "test run"));

        public Task<CustomLoopOrderedRunResult> ResumeAsync(
            GovernedLoopSequentialOrderedResumeRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new CustomLoopOrderedRunResult(CustomLoopOrderedRunStatus.Completed, run, "test resume"));

        public Task<CustomLoopOrderedRunResult> ResumeWaitAsync(
            GovernedLoopSequentialOrderedWaitResumeRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastWaitRequest = request;
            return Task.FromResult(new CustomLoopOrderedRunResult(CustomLoopOrderedRunStatus.Completed, run, "test Wait resume"));
        }
    }

    private sealed class StaticSequentialRunEvidenceSource(GovernedLoopSequentialRunEvidence? evidence)
        : IGovernedLoopSequentialRunEvidenceSource
    {
        public Task<GovernedLoopSequentialRunEvidence?> ResolveAsync(
            string runId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(evidence);
        }
    }

    private sealed record WaitRuntimeHarness(
        SequentialTestContext Context,
        FakeRunStore Store,
        StubGovernedLoopSleepTimeProvider Time,
        InMemoryGovernedLoopSleepStore SleepStore,
        CanonicalWaitPosturePort Posture,
        TestExecutionGate Gate,
        StubCapabilityAuthorityTransaction Authority,
        BoundWaitOrderedResumePort OrderedResume,
        GovernedLoopWaitExecutionService WaitService,
        GovernedLoopSleepService SleepService,
        IGovernedLoopSequentialOrderedRuntime Runtime,
        RecordingWaitNodeExecutor RecordingWait,
        SequentialEvidenceHarness Evidence,
        DateTimeOffset Deadline)
    {
        internal GovernedLoopSequentialNodeDispatchRequest? ParkRequest => RecordingWait.LastRequest;

        internal Task<CustomLoopOrderedRunResult> RunToParkAsync()
            => Runtime.RunAsync(new GovernedLoopSequentialOrderedRunRequest(
                GovernedLoopSequentialOrderedRunRequest.CurrentSchemaVersion,
                Context.Anchor,
                Context.Plan,
                Context.Artifact,
                AuditSchema.Actors.Web));

        internal Task<GovernedLoopWakeResult> WakeAsync()
        {
            Time.UtcNow = Deadline;
            var park = Assert.IsType<GovernedLoopWaitParkEvidence>(Assert.Single(Store.Current.WaitEvidence).ParkEvidence);
            return SleepService.WakeAsync(new GovernedLoopWakeRequest(
                park.Checkpoint.CheckpointId,
                park.Checkpoint.ContentHash,
                null));
        }
    }

    public enum WaitCrashBoundary
    {
        ParkRun,
        SleepCheckpoint,
        CheckpointAttachment,
        ContinuationRun,
        CompletionRun,
    }
}
