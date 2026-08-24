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
using EmbodySense.Core.Common.Loops.Failures.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Sequential;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Custom;

/// <summary>Exercises retry behavior through the canonical ordered-runner and durable sleep boundaries.</summary>
public sealed partial class CustomLoopOrderedRunnerTests
{
    [Fact]
    public async Task Retry_safe_failure_parks_then_wakes_one_exact_bounded_next_attempt()
    {
        var context = await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: RetryArtifact);
        var store = new FakeRunStore(context.Run);
        var time = new StubGovernedLoopSleepTimeProvider(_now);
        var sleepStore = new InMemoryGovernedLoopSleepStore();
        var sleepPosture = new CanonicalWaitPosturePort(store, context.Anchor.AdapterBinding.AdmissionReceipt.Intent.Publication, time);
        var continuationRelay = new GovernedLoopWaitContinuationRelay();
        var sleep = new GovernedLoopSleepService(
            sleepStore,
            sleepPosture,
            continuationRelay,
            new StubGovernedLoopAuthenticatedWakeVerificationPort(),
            time);
        var retryPosture = new CanonicalRetryPosturePort(time);
        var orderedResume = new BoundRetryOrderedResumePort();
        var retry = new GovernedLoopRetryExecutionService(store, sleep, retryPosture, orderedResume);
        var recordingRetry = new RecordingRetryNodeExecutor(retry);
        var retryRelay = new GovernedLoopRetryNodeExecutionRelay();
        retryRelay.Bind(recordingRetry);
        continuationRelay.BindRetry(retry);

        var failFirst = true;
        var executor = new QueueExecutor(Result("retry completed"))
        {
            BeforeProviderRequestStarted = _ =>
            {
                if (failFirst)
                {
                    failFirst = false;
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

        Assert.True(
            parked.Status == CustomLoopOrderedRunStatus.Waiting,
            $"{parked.Status}: {parked.Detail}; {store.Current.FailureCode}/{store.Current.FailureDetail}; retry={recordingRetry.LastException?.Message}; validation={string.Join(" | ", store.ValidationFailures.Select(item => item.Code + ":" + item.Field))}");
        Assert.Equal(CustomLoopRunStatus.Waiting, store.Current.Status);
        Assert.Single(executor.Requests);
        Assert.Equal(0, executor.ProviderRequestStartedCount);
        var scheduled = Assert.IsType<GovernedLoopRetryState>(store.Current.Events.Last(item => item.RetryState is not null).RetryState);
        Assert.Equal(GovernedLoopRetryStateDisposition.Scheduled, scheduled.Disposition);
        Assert.Equal(2, scheduled.NextAttempt);
        Assert.NotNull(scheduled.WakeCheckpointId);
        Assert.NotNull(scheduled.WakeCheckpointHash);
        Assert.Equal(1, sleepStore.CheckpointCount);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);

        var scheduledEvent = store.Current.Events.Last(item => item.RetryState is not null);
        var staleDue = GovernedLoopRetryContract.CreateState(
            scheduled.Identity,
            scheduled.StateVersion + 1,
            GovernedLoopRetryStateDisposition.Due,
            scheduled.CurrentAttempt,
            scheduled.CurrentAttemptOperationId,
            scheduled.NextAttempt,
            scheduled.AttemptOperationId,
            scheduled.Budget,
            null,
            scheduled.WakeCheckpointId,
            scheduled.WakeCheckpointHash,
            scheduled.FailureEvidenceId,
            scheduled.FailureEvidenceHash,
            scheduled.RecordedAtUtc);
        var staleDueEvent = scheduledEvent with
        {
            Sequence = store.Current.Events.Length + 1,
            EventId = $"retry-{scheduled.Identity.SeriesId[..16]}-{staleDue.StateVersion}",
            Detail = "A forged due state retained a stale Waiting frontier.",
            RetryState = staleDue,
        };
        var staleWaiting = store.Current with { Events = [.. store.Current.Events, staleDueEvent] };
        Assert.Contains(
            CustomLoopRunValidator.Validate(staleWaiting).Errors,
            error => string.Equals(error.Code, "waiting_run_evidence_required", StringComparison.Ordinal));

        time.UtcNow = scheduled.NextRetryAtUtc!.Value;
        var wake = await sleep.WakeAsync(new GovernedLoopWakeRequest(
            scheduled.WakeCheckpointId!,
            scheduled.WakeCheckpointHash!,
            null));

        Assert.True(
            wake.Status == GovernedLoopWakeResultStatus.Committed,
            $"{wake.Status}; run={store.Current.Status}; failure={store.Current.FailureCode}/{store.Current.FailureDetail}; retry={string.Join(",", store.Current.Events.Where(item => item.RetryState is not null).Select(item => item.RetryState!.Disposition))}; validation={string.Join(" | ", store.ValidationFailures.Select(item => item.Code + ":" + item.Field))}");
        Assert.Equal(CustomLoopRunStatus.Completed, store.Current.Status);
        Assert.Equal("retry completed", store.Current.FinalOutput);
        Assert.Equal(2, executor.Requests.Count);
        Assert.Equal(1, executor.ProviderRequestStartedCount);
        Assert.Equal([1, 2], store.Current.Events
            .Where(item => item.StepId == "infer-01" && item.SequentialNodeEvidence?.Kind == CustomLoopSequentialNodeEvidenceKind.DispatchStarted)
            .Select(item => item.SequentialNodeEvidence!.Attempt!.Value)
            .ToArray());
        Assert.Equal(
            [
                GovernedLoopRetryStateDisposition.FailureRetained,
                GovernedLoopRetryStateDisposition.Scheduled,
                GovernedLoopRetryStateDisposition.Scheduled,
                GovernedLoopRetryStateDisposition.Due,
                GovernedLoopRetryStateDisposition.Reserved,
                GovernedLoopRetryStateDisposition.Dispatched,
            ],
            store.Current.Events.Where(item => item.RetryState is not null).Select(item => item.RetryState!.Disposition).ToArray());
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
        Assert.Empty(store.ValidationFailures);

        var replay = await sleep.WakeAsync(new GovernedLoopWakeRequest(
            scheduled.WakeCheckpointId!,
            scheduled.WakeCheckpointHash!,
            null));

        Assert.Equal(GovernedLoopWakeResultStatus.Duplicate, replay.Status);
        Assert.Equal(2, executor.Requests.Count);
    }

    [Fact]
    public async Task Retry_wake_stops_durably_without_redispatch_when_current_authority_is_revoked()
    {
        var context = await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: RetryArtifact);
        var store = new FakeRunStore(context.Run);
        var time = new StubGovernedLoopSleepTimeProvider(_now);
        var sleepStore = new InMemoryGovernedLoopSleepStore();
        var sleepPosture = new CanonicalWaitPosturePort(store, context.Anchor.AdapterBinding.AdmissionReceipt.Intent.Publication, time);
        var continuationRelay = new GovernedLoopWaitContinuationRelay();
        var sleep = new GovernedLoopSleepService(
            sleepStore,
            sleepPosture,
            continuationRelay,
            new StubGovernedLoopAuthenticatedWakeVerificationPort(),
            time);
        var retryPosture = new CanonicalRetryPosturePort(time);
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
        orderedResume.Bind(new GovernedLoopWaitOrderedContext(context.Anchor, context.Plan, context.Artifact), runtime);

        var parked = await runtime.RunAsync(new GovernedLoopSequentialOrderedRunRequest(
            GovernedLoopSequentialOrderedRunRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            AuditSchema.Actors.Web));
        var scheduled = Assert.IsType<GovernedLoopRetryState>(store.Current.Events.Last(item => item.RetryState is not null).RetryState);
        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        retryPosture.AuthorityEligible = false;
        time.UtcNow = scheduled.NextRetryAtUtc!.Value;

        var wake = await sleep.WakeAsync(new GovernedLoopWakeRequest(
            scheduled.WakeCheckpointId!,
            scheduled.WakeCheckpointHash!,
            null));

        Assert.True(
            wake.Status == GovernedLoopWakeResultStatus.Committed,
            $"{wake.Status}/{wake.Evidence?.DispositionEvidenceReference}; run={store.Current.Status}; failure={store.Current.FailureCode}/{store.Current.FailureDetail}; events={string.Join(" | ", store.Current.Events.Where(item => item.FailureEvidence is not null).Select(item => $"{item.Kind}/{item.SequentialNodeEvidence?.Kind}/{item.SequentialNodeEvidence?.Disposition}/{CustomLoopSequentialOutcomeArtifactHash.Matches(item)}"))}; validation={string.Join(" | ", store.ValidationFailures.Select(item => item.Code + ":" + item.Field))}");
        Assert.Equal(CustomLoopRunStatus.NeedsReview, store.Current.Status);
        Assert.Equal("canonical_retry_current_posture_ineligible", store.Current.FailureCode);
        Assert.Equal(GovernedLoopRetryStateDisposition.Stopped, store.Current.Events.Last(item => item.RetryState is not null).RetryState?.Disposition);
        Assert.Single(executor.Requests);
        Assert.Equal(0, executor.ProviderRequestStartedCount);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
        Assert.Empty(store.ValidationFailures);
    }

    [Fact]
    public async Task Retry_wake_exhausts_durably_with_classified_no_dispatch_evidence()
    {
        var context = await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: RetryArtifact);
        var store = new FakeRunStore(context.Run);
        var time = new StubGovernedLoopSleepTimeProvider(_now);
        var sleepStore = new InMemoryGovernedLoopSleepStore();
        var sleepPosture = new CanonicalWaitPosturePort(store, context.Anchor.AdapterBinding.AdmissionReceipt.Intent.Publication, time);
        var continuationRelay = new GovernedLoopWaitContinuationRelay();
        var sleep = new GovernedLoopSleepService(
            sleepStore,
            sleepPosture,
            continuationRelay,
            new StubGovernedLoopAuthenticatedWakeVerificationPort(),
            time);
        var retryPosture = new CanonicalRetryPosturePort(time);
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
        orderedResume.Bind(new GovernedLoopWaitOrderedContext(context.Anchor, context.Plan, context.Artifact), runtime);

        var parked = await runtime.RunAsync(new GovernedLoopSequentialOrderedRunRequest(
            GovernedLoopSequentialOrderedRunRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            AuditSchema.Actors.Web));
        var scheduled = Assert.IsType<GovernedLoopRetryState>(store.Current.Events.Last(item => item.RetryState is not null).RetryState);
        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        retryPosture.Budget = scheduled.Budget with { ResourceUnits = 2 };
        time.UtcNow = scheduled.NextRetryAtUtc!.Value;

        var wake = await sleep.WakeAsync(new GovernedLoopWakeRequest(
            scheduled.WakeCheckpointId!,
            scheduled.WakeCheckpointHash!,
            null));

        Assert.True(
            wake.Status == GovernedLoopWakeResultStatus.Committed,
            $"{wake.Status}/{wake.Evidence?.DispositionEvidenceReference}; run={store.Current.Status}; failure={store.Current.FailureCode}/{store.Current.FailureDetail}; retry={string.Join(',', store.Current.Events.Where(item => item.RetryState is not null).Select(item => item.RetryState!.Disposition))}; validation={string.Join(" | ", store.ValidationFailures.Select(item => item.Code + ":" + item.Field))}");
        Assert.Equal(CustomLoopRunStatus.Failed, store.Current.Status);
        Assert.Equal(GovernedLoopFrontierStatus.Failed, store.Current.Frontier?.Payload.Status);
        Assert.Equal("canonical_retry_budget_exhausted", store.Current.FailureCode);
        var terminalStateEvent = store.Current.Events.Last(item => item.RetryState is not null);
        Assert.Equal(GovernedLoopRetryStateDisposition.Exhausted, terminalStateEvent.RetryState?.Disposition);
        var exhaustion = Assert.Single(store.Current.Events, item => item.FailureEvidence?.FailureClass == GovernedLoopFailureClass.Exhaustion);
        Assert.Equal(2, exhaustion.Attempt);
        Assert.Equal(GovernedLoopFailureEffectCertainty.DispatchProvedNotStarted, exhaustion.FailureEvidence?.EffectCertainty);
        Assert.Equal(terminalStateEvent.EventId, exhaustion.FailureEvidence?.CausalEvidence.Single().EvidenceId);
        Assert.Equal(terminalStateEvent.RetryState?.ContentHash, exhaustion.FailureEvidence?.CausalEvidence.Single().EvidenceHash);
        Assert.Single(executor.Requests);
        Assert.Equal(0, executor.ProviderRequestStartedCount);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
        Assert.Empty(store.ValidationFailures);
    }

    [Fact]
    public async Task Retry_wake_forwards_every_remaining_resource_ceiling_to_the_next_provider_attempt()
    {
        var context = await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role => RetryArtifact(role, 2, 10_000, 10, 3, 1_000, "USD"));
        var store = new FakeRunStore(context.Run);
        var time = new StubGovernedLoopSleepTimeProvider(_now);
        var sleepStore = new InMemoryGovernedLoopSleepStore();
        var sleepPosture = new CanonicalWaitPosturePort(store, context.Anchor.AdapterBinding.AdmissionReceipt.Intent.Publication, time);
        var continuationRelay = new GovernedLoopWaitContinuationRelay();
        var sleep = new GovernedLoopSleepService(
            sleepStore,
            sleepPosture,
            continuationRelay,
            new StubGovernedLoopAuthenticatedWakeVerificationPort(),
            time);
        var retryPosture = new CanonicalRetryPosturePort(time)
        {
            Budget = new GovernedLoopRetryBudgetSnapshot(1, 4, 1, 600, "USD", 1),
        };
        var orderedResume = new BoundRetryOrderedResumePort();
        var retry = new GovernedLoopRetryExecutionService(store, sleep, retryPosture, orderedResume);
        var retryRelay = new GovernedLoopRetryNodeExecutionRelay();
        retryRelay.Bind(retry);
        continuationRelay.BindRetry(retry);
        var first = true;
        var executor = new QueueExecutor(Result("retry completed"))
        {
            BeforeProviderRequestStarted = _ =>
            {
                if (first)
                {
                    first = false;
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
        time.UtcNow = scheduled.NextRetryAtUtc!.Value;
        var wake = await sleep.WakeAsync(new GovernedLoopWakeRequest(
            scheduled.WakeCheckpointId!,
            scheduled.WakeCheckpointHash!,
            null));

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        Assert.Equal(GovernedLoopWakeResultStatus.Committed, wake.Status);
        var boundedRetry = Assert.IsType<CustomLoopRetryDispatchBudget>(executor.Requests[1].RetryDispatchBudget);
        Assert.Equal(6, boundedRetry.RemainingTokens);
        Assert.Equal(2, boundedRetry.RemainingToolCalls);
        Assert.Equal(400, boundedRetry.RemainingCostMicrounits);
        Assert.Equal("USD", boundedRetry.CostCurrency);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
        Assert.Empty(store.ValidationFailures);
    }

    [Fact]
    public async Task Retry_wake_exhaustion_routes_through_the_exact_failure_edge_before_terminalizing_the_run()
    {
        var context = await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: RetryFailureRouteArtifact);
        var store = new FakeRunStore(context.Run);
        var time = new StubGovernedLoopSleepTimeProvider(_now);
        var sleepStore = new InMemoryGovernedLoopSleepStore();
        var sleepPosture = new CanonicalWaitPosturePort(store, context.Anchor.AdapterBinding.AdmissionReceipt.Intent.Publication, time);
        var continuationRelay = new GovernedLoopWaitContinuationRelay();
        var sleep = new GovernedLoopSleepService(
            sleepStore,
            sleepPosture,
            continuationRelay,
            new StubGovernedLoopAuthenticatedWakeVerificationPort(),
            time);
        var retryPosture = new CanonicalRetryPosturePort(time);
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
        orderedResume.Bind(new GovernedLoopWaitOrderedContext(context.Anchor, context.Plan, context.Artifact), runtime);

        var parked = await runtime.RunAsync(new GovernedLoopSequentialOrderedRunRequest(
            GovernedLoopSequentialOrderedRunRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            AuditSchema.Actors.Web));
        var scheduled = Assert.IsType<GovernedLoopRetryState>(store.Current.Events.Last(item => item.RetryState is not null).RetryState);
        retryPosture.Budget = scheduled.Budget with { ResourceUnits = 2 };
        time.UtcNow = scheduled.NextRetryAtUtc!.Value;

        var wake = await sleep.WakeAsync(new GovernedLoopWakeRequest(
            scheduled.WakeCheckpointId!,
            scheduled.WakeCheckpointHash!,
            null));

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        Assert.True(wake.Status == GovernedLoopWakeResultStatus.Committed, wake.Evidence?.DispositionEvidenceReference);
        var routed = store.Writes.First(candidate => candidate.Status == CustomLoopRunStatus.Running
            && candidate.Frontier?.Payload.Status == GovernedLoopFrontierStatus.Active
            && candidate.Events.LastOrDefault(item => item.RetryState is not null)?.RetryState?.Disposition == GovernedLoopRetryStateDisposition.Exhausted);
        var inference = routed.Frontier!.Payload.Nodes.Single(item => string.Equals(item.NodeId, "infer-01", StringComparison.Ordinal));
        Assert.Equal(GovernedLoopNodeExecutionStatus.Failed, inference.Status);
        Assert.Equal(["infer-01-to-fail"], inference.SelectedControlEdgeIds);
        Assert.Equal(CustomLoopRunStatus.Failed, store.Current.Status);
        var fail = store.Current.Frontier!.Payload.Nodes.Single(item => string.Equals(item.NodeId, "fail", StringComparison.Ordinal));
        Assert.Equal(GovernedLoopNodeExecutionStatus.Failed, fail.Status);
        Assert.Single(executor.Requests);
        Assert.Equal(0, executor.ProviderRequestStartedCount);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
        Assert.Empty(store.ValidationFailures);
    }

    [Theory]
    [InlineData(true, GovernedLoopRetryExecutionStatus.Exhausted)]
    [InlineData(false, GovernedLoopRetryExecutionStatus.Ineligible)]
    public async Task Retry_terminal_schedule_decision_replays_after_response_loss_without_appending_evidence(
        bool exhausted,
        GovernedLoopRetryExecutionStatus expectedStatus)
    {
        var context = await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role => RetryArtifact(role, 2));
        CustomLoopRunRecord? terminalSnapshot = null;
        var store = new FakeRunStore(context.Run)
        {
            AfterUpdate = candidate =>
            {
                if (candidate.Events.LastOrDefault(item => item.RetryState is not null)?.RetryState?.Disposition
                    is GovernedLoopRetryStateDisposition.Exhausted or GovernedLoopRetryStateDisposition.Stopped)
                {
                    terminalSnapshot = candidate;
                }

                return Task.CompletedTask;
            },
        };
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
            AuthorityEligible = exhausted,
            Budget = exhausted ? new GovernedLoopRetryBudgetSnapshot(1, 0, 0, null, null, 2) : null,
        };
        var orderedResume = new BoundRetryOrderedResumePort();
        var retry = new GovernedLoopRetryExecutionService(store, sleep, retryPosture, orderedResume);
        var retryRelay = new GovernedLoopRetryNodeExecutionRelay();
        retryRelay.Bind(retry);
        continuationRelay.BindRetry(retry);
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var runtime = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(
                store,
                new QueueExecutor(Result("must not dispatch"))
                {
                    BeforeProviderRequestStarted = _ => throw new InvalidOperationException("provider unavailable before transport"),
                },
                timeProvider: time,
                retryNodeExecutor: retryRelay),
            evidence,
            evidence);
        orderedResume.Bind(new GovernedLoopWaitOrderedContext(context.Anchor, context.Plan, context.Artifact), runtime);

        await runtime.RunAsync(new GovernedLoopSequentialOrderedRunRequest(
            GovernedLoopSequentialOrderedRunRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            AuditSchema.Actors.Web));

        var retained = Assert.IsType<CustomLoopRunRecord>(terminalSnapshot);
        var retainedFailure = Assert.IsType<GovernedLoopFailureEvidence>(retained.Events.Last(item => item.FailureEvidence is not null).FailureEvidence);
        var retainedNode = context.Plan.Nodes.Single(item => string.Equals(item.NodeId, retainedFailure.NodeId, StringComparison.Ordinal));
        var replayStore = new FakeRunStore(retained);
        var replaySleep = new GovernedLoopSleepService(
            new InMemoryGovernedLoopSleepStore(),
            new CanonicalWaitPosturePort(replayStore, context.Anchor.AdapterBinding.AdmissionReceipt.Intent.Publication, time),
            new GovernedLoopWaitContinuationRelay(),
            new StubGovernedLoopAuthenticatedWakeVerificationPort(),
            time);
        var replay = await new GovernedLoopRetryExecutionService(replayStore, replaySleep, retryPosture, new BoundRetryOrderedResumePort()).ScheduleAsync(
            new GovernedLoopRetryExecutionRequest(context.Anchor, context.Plan, retainedNode, retainedFailure, AuditSchema.Actors.Web));

        Assert.Equal(expectedStatus, replay.Status);
        Assert.Equal("retry-terminal-decision-replayed", replay.Detail);
        Assert.Equal(retained, replay.Run);
        Assert.Empty(replayStore.Writes);
        Assert.True(CustomLoopRunValidator.Validate(replayStore.Current).IsValid);
    }

    [Fact]
    public async Task Retry_recovery_attaches_a_published_checkpoint_after_interrupted_run_update_without_duplicate_dispatch()
    {
        var context = await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: RetryArtifact);
        var interruptAttachment = true;
        var store = new FakeRunStore(context.Run)
        {
            BeforeUpdate = (candidate, _) =>
            {
                if (interruptAttachment
                    && candidate.Events.LastOrDefault()?.RetryState is
                    {
                        Disposition: GovernedLoopRetryStateDisposition.Scheduled,
                        WakeCheckpointId: not null,
                    })
                {
                    interruptAttachment = false;
                    throw new IOException("simulated process loss before checkpoint attachment");
                }
            },
        };
        var time = new StubGovernedLoopSleepTimeProvider(_now);
        var sleepStore = new InMemoryGovernedLoopSleepStore();
        var sleepPosture = new CanonicalWaitPosturePort(store, context.Anchor.AdapterBinding.AdmissionReceipt.Intent.Publication, time);
        var continuationRelay = new GovernedLoopWaitContinuationRelay();
        var sleep = new GovernedLoopSleepService(
            sleepStore,
            sleepPosture,
            continuationRelay,
            new StubGovernedLoopAuthenticatedWakeVerificationPort(),
            time);
        var orderedResume = new BoundRetryOrderedResumePort();
        var retry = new GovernedLoopRetryExecutionService(store, sleep, new CanonicalRetryPosturePort(time), orderedResume);
        var retryRelay = new GovernedLoopRetryNodeExecutionRelay();
        retryRelay.Bind(retry);
        continuationRelay.BindRetry(retry);
        var failFirst = true;
        var executor = new QueueExecutor(Result("recovered retry completed"))
        {
            BeforeProviderRequestStarted = _ =>
            {
                if (failFirst)
                {
                    failFirst = false;
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

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        Assert.Equal(CustomLoopRunStatus.Waiting, store.Current.Status);
        Assert.Equal(1, sleepStore.CheckpointCount);
        var pending = Assert.IsType<GovernedLoopRetryState>(store.Current.Events.Last(item => item.RetryState is not null).RetryState);
        Assert.Null(pending.WakeCheckpointId);

        var recovery = await retry.RecoverAsync(10);

        Assert.Equal(1, recovery.Recovered);
        Assert.Equal(0, recovery.NeedsReview);
        var attached = Assert.IsType<GovernedLoopRetryState>(store.Current.Events.Last(item => item.RetryState is not null).RetryState);
        Assert.NotNull(attached.WakeCheckpointId);
        Assert.Equal(1, sleepStore.CheckpointCount);
        time.UtcNow = attached.NextRetryAtUtc!.Value;

        var wake = await sleep.WakeAsync(new GovernedLoopWakeRequest(
            attached.WakeCheckpointId!,
            attached.WakeCheckpointHash!,
            null));

        Assert.Equal(GovernedLoopWakeResultStatus.Committed, wake.Status);
        Assert.Equal(CustomLoopRunStatus.Completed, store.Current.Status);
        Assert.Equal("recovered retry completed", store.Current.FinalOutput);
        Assert.Equal(2, executor.Requests.Count);
        Assert.Equal(1, executor.ProviderRequestStartedCount);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
        Assert.Empty(store.ValidationFailures);
    }

    [Fact]
    public async Task Retry_two_safe_failures_preserve_one_series_and_dispatch_the_third_attempt_once()
    {
        var context = await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role => RetryArtifact(role, 3));
        var store = new FakeRunStore(context.Run);
        var time = new StubGovernedLoopSleepTimeProvider(_now);
        var sleepStore = new InMemoryGovernedLoopSleepStore();
        var sleepPosture = new CanonicalWaitPosturePort(store, context.Anchor.AdapterBinding.AdmissionReceipt.Intent.Publication, time);
        var continuationRelay = new GovernedLoopWaitContinuationRelay();
        var sleep = new GovernedLoopSleepService(
            sleepStore,
            sleepPosture,
            continuationRelay,
            new StubGovernedLoopAuthenticatedWakeVerificationPort(),
            time);
        var orderedResume = new BoundRetryOrderedResumePort();
        var retry = new GovernedLoopRetryExecutionService(store, sleep, new CanonicalRetryPosturePort(time), orderedResume);
        var retryRelay = new GovernedLoopRetryNodeExecutionRelay();
        retryRelay.Bind(retry);
        continuationRelay.BindRetry(retry);
        var failuresRemaining = 2;
        var executor = new QueueExecutor(Result("third attempt completed"))
        {
            BeforeProviderRequestStarted = _ =>
            {
                if (failuresRemaining > 0)
                {
                    failuresRemaining--;
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

        var firstPark = await runtime.RunAsync(new GovernedLoopSequentialOrderedRunRequest(
            GovernedLoopSequentialOrderedRunRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, firstPark.Status);
        var firstSchedule = Assert.IsType<GovernedLoopRetryState>(store.Current.Events.Last(item => item.RetryState is not null).RetryState);
        time.UtcNow = firstSchedule.NextRetryAtUtc!.Value;
        var firstWake = await sleep.WakeAsync(new GovernedLoopWakeRequest(
            firstSchedule.WakeCheckpointId!,
            firstSchedule.WakeCheckpointHash!,
            null));

        Assert.Equal(GovernedLoopWakeResultStatus.Committed, firstWake.Status);
        Assert.Equal(CustomLoopRunStatus.Waiting, store.Current.Status);
        var secondSchedule = Assert.IsType<GovernedLoopRetryState>(store.Current.Events.Last(item => item.RetryState is not null).RetryState);
        Assert.Equal(GovernedLoopRetryStateDisposition.Scheduled, secondSchedule.Disposition);
        Assert.Equal(3, secondSchedule.NextAttempt);
        time.UtcNow = secondSchedule.NextRetryAtUtc!.Value;
        var secondWake = await sleep.WakeAsync(new GovernedLoopWakeRequest(
            secondSchedule.WakeCheckpointId!,
            secondSchedule.WakeCheckpointHash!,
            null));

        Assert.Equal(GovernedLoopWakeResultStatus.Committed, secondWake.Status);
        Assert.Equal(CustomLoopRunStatus.Completed, store.Current.Status);
        Assert.Equal("third attempt completed", store.Current.FinalOutput);
        Assert.Equal(3, executor.Requests.Count);
        Assert.Equal(1, executor.ProviderRequestStartedCount);
        var retryStates = store.Current.Events.Where(item => item.RetryState is not null).Select(item => item.RetryState!).ToArray();
        Assert.Single(retryStates.Select(item => item.Identity.SeriesId).Distinct(StringComparer.Ordinal));
        Assert.Equal([2, 3], retryStates
            .Where(item => item.Disposition == GovernedLoopRetryStateDisposition.Dispatched)
            .Select(item => item.NextAttempt)
            .ToArray());
        Assert.Equal(2, retryStates
            .Where(item => item.Disposition == GovernedLoopRetryStateDisposition.Dispatched)
            .Select(item => item.CurrentAttemptOperationId)
            .Distinct(StringComparer.Ordinal)
            .Count());
        Assert.Equal([1, 2, 3], store.Current.Events
            .Where(item => item.StepId == "infer-01" && item.SequentialNodeEvidence?.Kind == CustomLoopSequentialNodeEvidenceKind.DispatchStarted)
            .Select(item => item.SequentialNodeEvidence!.Attempt!.Value)
            .ToArray());
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
        Assert.Empty(store.ValidationFailures);
    }

    [Fact]
    public async Task Retry_per_attempt_timeout_cancels_before_transport_and_exhausts_without_provider_dispatch()
    {
        var context = await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role => RetryArtifact(role, 2, 10));
        var store = new FakeRunStore(context.Run);
        var time = new StubGovernedLoopSleepTimeProvider(_now);
        var sleepStore = new InMemoryGovernedLoopSleepStore();
        var sleepPosture = new CanonicalWaitPosturePort(store, context.Anchor.AdapterBinding.AdmissionReceipt.Intent.Publication, time);
        var continuationRelay = new GovernedLoopWaitContinuationRelay();
        var sleep = new GovernedLoopSleepService(
            sleepStore,
            sleepPosture,
            continuationRelay,
            new StubGovernedLoopAuthenticatedWakeVerificationPort(),
            time);
        var orderedResume = new BoundRetryOrderedResumePort();
        var retry = new GovernedLoopRetryExecutionService(store, sleep, new CanonicalRetryPosturePort(time), orderedResume);
        var retryRelay = new GovernedLoopRetryNodeExecutionRelay();
        retryRelay.Bind(retry);
        continuationRelay.BindRetry(retry);
        var executor = new QueueExecutor(Result("must not dispatch"), Result("must not dispatch"))
        {
            BeforeProviderRequestStarted = _ => Task.Delay(100),
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
        time.UtcNow = scheduled.NextRetryAtUtc!.Value;
        var wake = await sleep.WakeAsync(new GovernedLoopWakeRequest(
            scheduled.WakeCheckpointId!,
            scheduled.WakeCheckpointHash!,
            null));

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        Assert.True(
            wake.Status == GovernedLoopWakeResultStatus.Committed,
            $"{wake.Status}/{wake.Evidence?.DispositionEvidenceReference}; run={store.Current.Status}; failure={store.Current.FailureCode}/{store.Current.FailureDetail}; retry={string.Join(',', store.Current.Events.Where(item => item.RetryState is not null).Select(item => item.RetryState!.Disposition))}; validation={string.Join(" | ", store.ValidationFailures.Select(item => item.Code + ":" + item.Field))}");
        Assert.Equal(CustomLoopRunStatus.Failed, store.Current.Status);
        Assert.Equal(2, executor.Requests.Count);
        Assert.Equal(0, executor.ProviderRequestStartedCount);
        Assert.Equal(GovernedLoopRetryStateDisposition.Exhausted, store.Current.Events.Last(item => item.RetryState is not null).RetryState?.Disposition);
        Assert.All(
            store.Current.Events.Where(item => item.FailureEvidence is { FailureClass: not GovernedLoopFailureClass.Exhaustion }),
            item => Assert.Equal(GovernedLoopFailureEffectCertainty.DispatchProvedNotStarted, item.FailureEvidence?.EffectCertainty));
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
        Assert.Empty(store.ValidationFailures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retry_per_attempt_timeout_cancels_workspace_and_command_action_dispatch(bool commandAction)
    {
        var context = await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role => RetryActionArtifact(role, commandAction));
        var store = new FakeRunStore(context.Run);
        var time = new StubGovernedLoopSleepTimeProvider(_now);
        var action = new BlockingCancellationAwareActionExecutor();
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var runtime = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(
                store,
                new QueueExecutor(Result("bounded provider output")),
                timeProvider: time,
                workspaceActionExecutor: commandAction ? null : action,
                commandActionExecutor: commandAction ? action : null),
            evidence,
            evidence);

        var result = await runtime.RunAsync(new GovernedLoopSequentialOrderedRunRequest(
            GovernedLoopSequentialOrderedRunRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal(commandAction ? 0 : 1, action.WorkspaceRequests);
        Assert.Equal(commandAction ? 1 : 0, action.CommandRequests);
        Assert.True(action.CancellationObserved);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, store.Current.Status);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
        Assert.Empty(store.ValidationFailures);
    }

    [Fact]
    public async Task Retry_deadline_that_elapses_during_transport_preparation_stops_at_the_exact_provider_boundary()
    {
        var context = await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role => RetryArtifact(role, 2));
        var store = new FakeRunStore(context.Run);
        var time = new StubGovernedLoopSleepTimeProvider(_now);
        var sleepStore = new InMemoryGovernedLoopSleepStore();
        var sleepPosture = new CanonicalWaitPosturePort(store, context.Anchor.AdapterBinding.AdmissionReceipt.Intent.Publication, time);
        var continuationRelay = new GovernedLoopWaitContinuationRelay();
        var sleep = new GovernedLoopSleepService(
            sleepStore,
            sleepPosture,
            continuationRelay,
            new StubGovernedLoopAuthenticatedWakeVerificationPort(),
            time);
        var orderedResume = new BoundRetryOrderedResumePort();
        var retry = new GovernedLoopRetryExecutionService(store, sleep, new CanonicalRetryPosturePort(time), orderedResume);
        var retryRelay = new GovernedLoopRetryNodeExecutionRelay();
        retryRelay.Bind(retry);
        continuationRelay.BindRetry(retry);
        var failFirst = true;
        GovernedLoopRetryState? scheduled = null;
        var executor = new QueueExecutor(Result("must not dispatch"))
        {
            BeforeProviderRequestStarted = _ =>
            {
                if (failFirst)
                {
                    failFirst = false;
                    throw new InvalidOperationException("provider unavailable before transport");
                }

                time.UtcNow = scheduled!.Identity.DeadlineUtc;
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
        scheduled = Assert.IsType<GovernedLoopRetryState>(store.Current.Events.Last(item => item.RetryState is not null).RetryState);
        time.UtcNow = scheduled.NextRetryAtUtc!.Value;
        var wake = await sleep.WakeAsync(new GovernedLoopWakeRequest(
            scheduled.WakeCheckpointId!,
            scheduled.WakeCheckpointHash!,
            null));

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        Assert.Equal(GovernedLoopWakeResultStatus.Committed, wake.Status);
        Assert.Equal(CustomLoopRunStatus.Failed, store.Current.Status);
        Assert.Equal(2, executor.Requests.Count);
        Assert.Equal(0, executor.ProviderRequestStartedCount);
        Assert.Equal(GovernedLoopRetryStateDisposition.Exhausted, store.Current.Events.Last(item => item.RetryState is not null).RetryState?.Disposition);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
        Assert.Empty(store.ValidationFailures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retry_action_deadline_expiry_rejects_before_cancellation_ignoring_executor(bool commandAction)
    {
        var context = await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role => RetryActionArtifact(role, commandAction));
        var store = new FakeRunStore(context.Run);
        var time = new StubGovernedLoopSleepTimeProvider(_now);
        var sleepStore = new InMemoryGovernedLoopSleepStore();
        var sleepPosture = new CanonicalWaitPosturePort(store, context.Anchor.AdapterBinding.AdmissionReceipt.Intent.Publication, time);
        var continuationRelay = new GovernedLoopWaitContinuationRelay();
        var sleep = new GovernedLoopSleepService(
            sleepStore,
            sleepPosture,
            continuationRelay,
            new StubGovernedLoopAuthenticatedWakeVerificationPort(),
            time);
        var orderedResume = new BoundRetryOrderedResumePort();
        var retry = new GovernedLoopRetryExecutionService(store, sleep, new CanonicalRetryPosturePort(time), orderedResume);
        var retryRelay = new GovernedLoopRetryNodeExecutionRelay();
        retryRelay.Bind(retry);
        continuationRelay.BindRetry(retry);
        var action = new CancellationIgnoringRetryActionExecutor(commandAction);
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var runtime = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(
                store,
                new QueueExecutor(Result("unused provider output")),
                timeProvider: time,
                retryNodeExecutor: retryRelay,
                workspaceActionExecutor: commandAction ? null : action,
                commandActionExecutor: commandAction ? action : null),
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

        time.UtcNow = scheduled.NextRetryAtUtc!.Value;
        var actionNodeId = commandAction ? "command-action" : "workspace-action";
        store.BeforeUpdate = (candidate, _) =>
        {
            if (candidate.Events.Skip(store.Current.Events.Length).Any(item => item.Kind == CustomLoopRunEventKind.NodeAttemptStarted && string.Equals(item.StepId, actionNodeId, StringComparison.Ordinal)))
            {
                time.UtcNow = scheduled.Identity.DeadlineUtc;
            }
        };
        var wake = await sleep.WakeAsync(new GovernedLoopWakeRequest(
            scheduled.WakeCheckpointId!,
            scheduled.WakeCheckpointHash!,
            null));

        Assert.Equal(GovernedLoopWakeResultStatus.Committed, wake.Status);
        Assert.Equal(CustomLoopRunStatus.Failed, store.Current.Status);
        Assert.Equal("retry_deadline_exceeded", store.Current.FailureCode);
        Assert.Equal(1, action.RequestCount);
        var rejection = Assert.Single(store.Current.Events, item => item.FailureEvidence?.FailureClass == GovernedLoopFailureClass.Exhaustion && item.StepId == actionNodeId);
        Assert.Equal(GovernedLoopFailureEffectCertainty.NotApplicable, rejection.FailureEvidence?.EffectCertainty);
        Assert.Equal(CustomLoopSequentialNodeDisposition.Rejected, rejection.SequentialNodeEvidence?.Disposition);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
        Assert.Empty(store.ValidationFailures);
    }

    private static GovernedLoopGraphRevisionArtifact RetryArtifact(ContextualRoleRevisionPin owningRole)
        => RetryArtifact(owningRole, 2);

    private static GovernedLoopGraphRevisionArtifact RetryArtifact(ContextualRoleRevisionPin owningRole, int maximumAttempts)
        => RetryArtifact(owningRole, maximumAttempts, 10_000);

    private static GovernedLoopGraphRevisionArtifact RetryArtifact(
        ContextualRoleRevisionPin owningRole,
        int maximumAttempts,
        long perAttemptTimeoutMilliseconds,
        long? maximumTokens = null,
        int? maximumToolCalls = null,
        long? maximumCostMicrounits = null,
        string? maximumCostCurrency = null)
    {
        var source = GovernedLoopSequentialApplicationTestFixture.LinearArtifact(owningRole: owningRole).Graph;
        var inference = source.Nodes.Single(node => node.Descriptor == GovernedLoopSequentialNodeDescriptors.ProviderInference);
        var policy = GovernedLoopRetryContract.CreatePolicy(
            "retry-policy",
            inference.Id,
            [GovernedLoopFailureClass.DispatchProvedNotStarted],
            ["provider-dispatch-not-started"],
            maximumAttempts,
            perAttemptTimeoutMilliseconds,
            30_000,
            GovernedLoopRetryBackoffStrategy.Fixed,
            1_000,
            1_000,
            GovernedLoopRetryJitterStrategy.None,
            0,
            maximumTokens,
            maximumToolCalls,
            maximumCostMicrounits,
            maximumCostCurrency,
            maximumResourceUnits: maximumAttempts);
        var retryInference = new GovernedLoopNodeDefinition(
            inference.Id,
            inference.Descriptor,
            inference.Ports,
            inference.AuthorityCeiling,
            inference.Parameters,
            inference.ModelRoutingPolicy,
            inference.AuthoredInputDataClasses,
            policy);
        var nodes = source.Nodes.Select(node => string.Equals(node.Id, inference.Id, StringComparison.Ordinal)
            ? retryInference
            : node).ToArray();
        return GovernedLoopSequentialApplicationTestFixture.Artifact(
            nodes,
            source.ControlEdges,
            source.TerminalNodeIds,
            owningRole,
            source.Bindings,
            source.ValueSchemas,
            source.OutputContract,
            source.AuthorityCeiling);
    }

    private static GovernedLoopGraphRevisionArtifact RetryFailureRouteArtifact(ContextualRoleRevisionPin owningRole)
    {
        var retry = RetryArtifact(owningRole).Graph;
        var fail = GovernedLoopSequentialApplicationTestFixture.Node("fail", GovernedLoopSequentialNodeDescriptors.FailTerminal);
        return GovernedLoopSequentialApplicationTestFixture.Artifact(
            retry.Nodes.Append(fail).ToArray(),
            retry.ControlEdges.Append(new GovernedLoopControlEdgeDefinition("infer-01-to-fail", "infer-01", fail.Id, GovernedLoopControlCondition.Failure)).ToArray(),
            [.. retry.TerminalNodeIds, fail.Id],
            owningRole,
            retry.Bindings,
            retry.ValueSchemas,
            retry.OutputContract,
            retry.AuthorityCeiling);
    }

    private static GovernedLoopGraphRevisionArtifact RetryActionArtifact(ContextualRoleRevisionPin owningRole, bool commandAction)
    {
        var source = (commandAction
            ? GovernedLoopSequentialApplicationTestFixture.CommandActionArtifact(owningRole: owningRole)
            : GovernedLoopSequentialApplicationTestFixture.WorkspaceActionArtifact(owningRole: owningRole)).Graph;
        var actionId = commandAction ? "command-action" : "workspace-action";
        var action = source.Nodes.Single(node => string.Equals(node.Id, actionId, StringComparison.Ordinal));
        var policy = GovernedLoopRetryContract.CreatePolicy(
            "retry-action-policy",
            action.Id,
            [GovernedLoopFailureClass.DispatchProvedNotStarted],
            [],
            2,
            10,
            30_000,
            GovernedLoopRetryBackoffStrategy.Fixed,
            1_000,
            1_000,
            GovernedLoopRetryJitterStrategy.None,
            0,
            maximumResourceUnits: 2);
        var retryAction = new GovernedLoopNodeDefinition(
            action.Id,
            action.Descriptor,
            action.Ports,
            action.AuthorityCeiling,
            action.Parameters,
            action.ModelRoutingPolicy,
            action.AuthoredInputDataClasses,
            policy);
        return GovernedLoopSequentialApplicationTestFixture.Artifact(
            source.Nodes.Select(node => string.Equals(node.Id, action.Id, StringComparison.Ordinal) ? retryAction : node).ToArray(),
            source.ControlEdges,
            source.TerminalNodeIds,
            owningRole,
            source.Bindings,
            source.ValueSchemas,
            source.OutputContract,
            source.AuthorityCeiling);
    }

    private sealed class CanonicalRetryPosturePort(TimeProvider timeProvider) : IGovernedLoopRetryCurrentPosturePort
    {
        internal bool DependenciesEligible { get; set; } = true;

        internal Exception? Exception { get; set; }

        internal Action<CancellationToken>? BeforeRead { get; set; }

        internal bool AuthorityEligible { get; set; } = true;

        internal GovernedLoopRetryBudgetSnapshot? Budget { get; set; }

        internal bool LifecycleEligible { get; set; } = true;

        internal DateTimeOffset? ObservedAtUtc { get; set; }

        internal bool ReturnNull { get; set; }

        internal GovernedLoopRetryCurrentPostureReadStatus Status { get; set; } = GovernedLoopRetryCurrentPostureReadStatus.Found;

        public Task<GovernedLoopRetryCurrentPostureReadResult?> ReadAsync(
            CustomLoopRunRecord run,
            GovernedLoopRetryPolicy policy,
            GovernedLoopFailureEvidence failure,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforeRead?.Invoke(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (Exception is not null)
            {
                throw Exception;
            }
            if (ReturnNull)
            {
                return Task.FromResult<GovernedLoopRetryCurrentPostureReadResult?>(null);
            }
            return Task.FromResult<GovernedLoopRetryCurrentPostureReadResult?>(new GovernedLoopRetryCurrentPostureReadResult(
                Status,
                Status == GovernedLoopRetryCurrentPostureReadStatus.Found
                    ? new GovernedLoopRetryCurrentPosture(
                        LifecycleEligible,
                        AuthorityEligible,
                        DependenciesEligible,
                        Budget ?? new GovernedLoopRetryBudgetSnapshot(failure.Attempt, 0, 0, null, null, failure.Attempt),
                        ObservedAtUtc ?? timeProvider.GetUtcNow())
                    : null));
        }
    }

    private sealed class BoundRetryOrderedResumePort : IGovernedLoopRetryOrderedResumePort
    {
        private GovernedLoopWaitOrderedContext? _context;
        private IGovernedLoopSequentialOrderedRuntime? _runtime;

        internal Exception? ResolveException { get; set; }

        internal Action<CancellationToken>? BeforeResolve { get; set; }

        internal Exception? ResumeException { get; set; }

        internal bool ReturnNullContext { get; set; }

        internal void Bind(GovernedLoopWaitOrderedContext context, IGovernedLoopSequentialOrderedRuntime runtime)
        {
            _context = context;
            _runtime = runtime;
        }

        public Task<GovernedLoopWaitOrderedContext?> ResolveAsync(CustomLoopRunRecord run, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforeResolve?.Invoke(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (ResolveException is not null)
            {
                throw ResolveException;
            }
            return Task.FromResult<GovernedLoopWaitOrderedContext?>(
                !ReturnNullContext
                && _context is not null
                && string.Equals(_context.Anchor.AdapterBinding.ExecutionBinding.RunId, run.Id, StringComparison.Ordinal)
                    ? _context
                    : null);
        }

        public Task<CustomLoopOrderedRunResult> ResumeRetryAsync(
            GovernedLoopRetryOrderedResumeRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ResumeException is not null)
            {
                throw ResumeException;
            }

            return _runtime!.ResumeRetryAsync(
                new GovernedLoopSequentialOrderedRetryResumeRequest(
                    GovernedLoopSequentialOrderedRetryResumeRequest.CurrentSchemaVersion,
                    request.Context.Anchor,
                    request.Context.Plan,
                    request.Context.Artifact,
                    request.RetryState,
                    request.Actor),
                cancellationToken);
        }
    }

    private sealed class RecordingRetryNodeExecutor(IGovernedLoopRetryNodeExecutor target) : IGovernedLoopRetryNodeExecutor
    {
        internal Exception? LastException { get; private set; }

        public async Task<GovernedLoopRetryExecutionResult> ScheduleAsync(
            GovernedLoopRetryExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            try
            {
                return await target.ScheduleAsync(request, cancellationToken);
            }
            catch (Exception exception)
            {
                LastException = exception;
                throw;
            }
        }
    }

    private sealed class ThrowingRetryNodeExecutor : IGovernedLoopRetryNodeExecutor
    {
        public Task<GovernedLoopRetryExecutionResult> ScheduleAsync(
            GovernedLoopRetryExecutionRequest request,
            CancellationToken cancellationToken = default)
            => throw new IOException("simulated retry scheduler outage");
    }
}
