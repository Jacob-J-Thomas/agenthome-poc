using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Application.Triggers.Schedules;
using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Tests.Triggers.Schedules;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops.Execution.Sleep;
using EmbodySense.Core.Persistence.Triggers;
using EmbodySense.Core.Persistence.Triggers.Schedules;
using EmbodySense.Core.Startup.Loops.Execution.Sleep;
using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Startup.Tests.Triggers.Schedules;
using EmbodySense.Core.Startup.Triggers.Schedules;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

public sealed class GovernedLoopLocalCanonicalCompositionTests
{
    private static readonly DateTimeOffset _now = new(2026, 8, 11, 21, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Canonical_factory_owns_real_durable_stores_and_rehydrates_after_restart()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var clock = new SteppingCoordinatorTimeProvider(_now, TimeSpan.FromMilliseconds(10));
        await using var first = GovernedLoopLocalBackgroundRuntimeFactory.Create(
            paths,
            new UnusedScheduleEvidence(),
            new UnusedScheduleOverlap(),
            new ScheduleBoundaryTimeZone(),
            new UnusedTriggerAuthorizer(),
            new UnusedTriggerDispatcher(),
            new UnusedSleepPosture(),
            new UnusedWakeContinuation(),
            new UnusedWakeVerification(),
            WorkOptions("worker-a"),
            CoordinatorOptions("owner-a"),
            clock);

        var started = await first.StartAsync();
        var stopped = await first.StopAsync();
        var retained = await new GovernedLoopCoordinatorEvidenceStore(paths).ReadAsync("local-background");

        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Started, started.Status);
        Assert.Equal(GovernedLoopLocalCoordinatorStopStatus.Stopped, stopped.Status);
        Assert.Equal(GovernedLoopCoordinatorReadStatus.Found, retained!.Status);
        Assert.Equal(GovernedLoopCoordinatorStatus.Stopped, retained.Snapshot!.LatestLifecycle.Status);

        clock.Advance(TimeSpan.FromSeconds(2));
        await using var restarted = GovernedLoopLocalBackgroundRuntimeFactory.Create(
            paths,
            new UnusedScheduleEvidence(),
            new UnusedScheduleOverlap(),
            new ScheduleBoundaryTimeZone(),
            new UnusedTriggerAuthorizer(),
            new UnusedTriggerDispatcher(),
            new UnusedSleepPosture(),
            new UnusedWakeContinuation(),
            new UnusedWakeVerification(),
            WorkOptions("worker-b"),
            CoordinatorOptions("owner-b"),
            clock);

        var rehydrated = await restarted.StartAsync();
        var restopped = await restarted.StopAsync();

        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Started, rehydrated.Status);
        Assert.Equal(2, rehydrated.Snapshot!.Ownership.OwnershipEpoch);
        Assert.Equal(GovernedLoopLocalCoordinatorStopStatus.Stopped, restopped.Status);
    }

    [Fact]
    public async Task Canonical_factory_defers_exact_pending_schedule_finalization_before_background_dispatch()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var clock = new SteppingCoordinatorTimeProvider(ScheduleContractTestData.FirstUtc.AddMinutes(1), TimeSpan.FromMilliseconds(10));
        var request = PendingScheduleRequest();
        var envelope = request.InitialState.PendingDelivery!.Prepared!.Envelope;
        Assert.Equal(ScheduleStoreMutationStatus.Applied, (await new ScheduleStore(paths).CreateAsync(request)).Status);
        Assert.Equal(ScheduleStoreMutationStatus.Applied, (await new ScheduleStore(paths).CreateAsync(BlockerScheduleRequest())).Status);
        Assert.Equal(ScheduleDeliveryProvenanceStatus.PendingFinalization, (await new ScheduleStore(paths).ResolveAsync(envelope)).Status);
        Assert.True(TriggerDeliveryAdmissionRequestFactory.TryCreate(
            envelope,
            envelope.Loop,
            envelope.Adapter,
            true,
            envelope.ActorContext,
            envelope.Authority,
            ScheduleContractTestData.FirstUtc.AddSeconds(5),
            out var delivery,
            out _));
        var queue = new TriggerQueueStore(paths, timeProvider: clock);
        var admission = await new TriggerQueueAdmissionService(new TriggerDeliveryAdmissionService(queue), queue)
            .AdmitAsync(TriggerQueueAdmissionRequestFactory.Create(delivery!, TriggerQueueAdmissionMode.Queued, TriggerQueuePriority.Normal));
        var authorizer = new CountingTriggerAuthorizer();
        var dispatcher = new CountingTriggerDispatcher();
        await using var runtime = GovernedLoopLocalBackgroundRuntimeFactory.Create(
            paths,
            new UnusedScheduleEvidence(),
            new UnusedScheduleOverlap(),
            new ScheduleBoundaryTimeZone(),
            authorizer,
            dispatcher,
            new UnusedSleepPosture(),
            new UnusedWakeContinuation(),
            new UnusedWakeVerification(),
            WorkOptions("worker-pending"),
            CoordinatorOptions("owner-pending") with { CycleInterval = TimeSpan.FromSeconds(1) },
            clock);

        var started = await runtime.StartAsync();
        await WaitForReleasedLeaseAsync(queue, clock);
        var stopped = await runtime.StopAsync();
        var queued = Assert.Single((await queue.GetSnapshotAsync(clock.GetUtcNow())).Entries);

        Assert.Equal(TriggerQueueAdmissionStatus.Queued, admission.Status);
        Assert.Equal(GovernedLoopLocalCoordinatorStartStatus.Started, started.Status);
        Assert.Equal(GovernedLoopLocalCoordinatorStopStatus.Stopped, stopped.Status);
        Assert.Equal(TriggerQueueEntryState.Queued, queued.State);
        Assert.NotNull(queued.WorkerLease?.ReleasedAtUtc);
        Assert.Null(queued.Dispatch);
        Assert.Equal(0, authorizer.Reads);
        Assert.Equal(0, dispatcher.Dispatches);
    }

    private static ScheduleStoreCreateRequest PendingScheduleRequest()
    {
        var definition = ScheduleContractTestData.Definition();
        Assert.True(ScheduleContractHash.TryComputeDefinition(definition, out var definitionHash, out var definitionValidation),
            string.Join(',', definitionValidation.Errors.Select(error => error.Code)));
        var occurrence = ScheduleContractTestData.Occurrence();
        var prepared = ScheduleContractTestData.Prepared(
            occurrence,
            definitionHash: definitionHash!,
            definitionRevision: definition.Revision,
            scheduleId: definition.ScheduleId);
        var pending = ScheduleContractTestData.Pending(
            occurrence,
            prepared,
            ScheduleContractTestData.Result(prepared.CanonicalEnvelopeHash),
            definitionHash: definitionHash!,
            definitionRevision: definition.Revision,
            scheduleId: definition.ScheduleId);
        var state = ScheduleContractTestData.State(
            occurrence,
            pending,
            definitionRevision: definition.Revision,
            definitionHash: definitionHash!,
            scheduleId: definition.ScheduleId);
        Assert.True(ScheduleContractValidator.ValidateDefinitionStateComposition(definition, state).IsValid);
        return new ScheduleStoreCreateRequest(definition, state, definitionHash!);
    }

    private static async Task WaitForReleasedLeaseAsync(TriggerQueueStore queue, TimeProvider clock)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var entry = Assert.Single((await queue.GetSnapshotAsync(clock.GetUtcNow(), timeout.Token)).Entries);
            if (entry.WorkerLease?.ReleasedAtUtc is not null)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }

    private static ScheduleStoreCreateRequest BlockerScheduleRequest()
    {
        Assert.True(ScheduleId.TryParse("aaa-blocker", out var scheduleId));
        var definition = ScheduleContractTestData.Definition() with
        {
            ScheduleId = scheduleId!,
            Payload = new SchedulePayloadReference(
                "payload/aaa-blocker",
                ScheduleContractTestData.Definition().Payload.ContentHash),
        };
        Assert.True(ScheduleContractHash.TryComputeDefinition(definition, out var definitionHash, out var definitionValidation),
            string.Join(',', definitionValidation.Errors.Select(error => error.Code)));
        var state = ScheduleContractTestData.State(
            definitionRevision: definition.Revision,
            definitionHash: definitionHash!,
            lastClockObservedAtUtc: ScheduleContractTestData.FirstUtc.AddMinutes(2),
            scheduleId: definition.ScheduleId);
        Assert.True(ScheduleContractValidator.ValidateDefinitionStateComposition(definition, state).IsValid);
        return new ScheduleStoreCreateRequest(definition, state, definitionHash!);
    }

    private static GovernedLoopLocalWorkRunnerOptions WorkOptions(string workerId)
        => new(workerId, TimeSpan.FromMinutes(1), 2, 4);

    private static GovernedLoopLocalCoordinatorOptions CoordinatorOptions(string ownerId)
        => new(
            "local-background",
            ownerId,
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(1),
            1);

    private sealed class UnusedScheduleEvidence : IScheduleCurrentEvidencePort
    {
        public Task<ScheduleCurrentEvidenceResult> ResolveAsync(
            ScheduleDefinition definition,
            ScheduleOccurrence occurrence,
            DateTimeOffset observedAtUtc,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("A missing schedule must not resolve current evidence.");
    }

    private sealed class UnusedTriggerAuthorizer : ITriggerDispatchAuthorizer
    {
        public Task<TriggerDispatchAuthorization> AuthorizeAsync(
            TriggerDeliveryEnvelope envelope,
            DateTimeOffset evaluatedAtUtc,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("An empty queue must not authorize dispatch.");
    }

    private sealed class CountingTriggerAuthorizer : ITriggerDispatchAuthorizer
    {
        internal int Reads { get; private set; }

        public Task<TriggerDispatchAuthorization> AuthorizeAsync(
            TriggerDeliveryEnvelope envelope,
            DateTimeOffset evaluatedAtUtc,
            CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult(new TriggerDispatchAuthorization(
                TriggerDispatchAuthorizationStatus.Authorized,
                new string('a', 64),
                "exact current trigger evidence"));
        }
    }

    private sealed class CountingTriggerDispatcher : ITriggerWorkerDispatcher
    {
        internal int Dispatches { get; private set; }

        public Task<TriggerWorkerDispatchResult> DispatchAsync(
            TriggerDeliveryEnvelope envelope,
            TriggerDispatchEvidence intent,
            CancellationToken cancellationToken = default)
        {
            Dispatches++;
            return Task.FromResult(new TriggerWorkerDispatchResult(TriggerDispatchOutcome.Terminal, "unexpected dispatch"));
        }
    }

    private sealed class UnusedTriggerDispatcher : ITriggerWorkerDispatcher
    {
        public Task<TriggerWorkerDispatchResult> DispatchAsync(
            TriggerDeliveryEnvelope envelope,
            TriggerDispatchEvidence intent,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("An empty queue must not dispatch.");
    }

    private sealed class UnusedSleepPosture : IGovernedLoopSleepCurrentPosturePort
    {
        public Task<GovernedLoopSleepCurrentPostureReadResult?> ReadAsync(
            GovernedLoopExecutionBinding binding,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("A missing checkpoint must not read execution posture.");
    }

    private sealed class UnusedWakeContinuation : IGovernedLoopWakeContinuationPort
    {
        public Task<GovernedLoopWakeContinuationResult?> ContinueAsync(
            GovernedLoopWakeContinuationRequest request,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("A missing checkpoint must not continue.");

        public Task<GovernedLoopWakeContinuationResult?> ReconcileAsync(
            GovernedLoopWakeContinuationRequest request,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("A missing checkpoint must not reconcile a continuation.");
    }

    private sealed class UnusedWakeVerification : IGovernedLoopAuthenticatedWakeVerificationPort
    {
        public Task<GovernedLoopAuthenticatedWakeVerificationResult?> VerifyAsync(
            GovernedLoopAuthenticatedWakeVerificationRequest request,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("A missing checkpoint must not verify an authenticated wake.");
    }
}
