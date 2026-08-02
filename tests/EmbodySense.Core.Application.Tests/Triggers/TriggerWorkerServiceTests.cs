using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Application.Tests.Triggers;

public sealed class TriggerWorkerServiceTests
{
    private static readonly DateTimeOffset _now = TriggerAdmissionTestData.CreatedAtUtc.AddSeconds(4);

    [Fact]
    public async Task Empty_selection_does_not_revalidate_or_dispatch()
    {
        var state = new WorkerStateStub(TriggerWorkerSelectionStatus.Empty);
        var authorizer = new AuthorizerStub(Authorized());
        var dispatcher = new DispatcherStub(new TriggerWorkerDispatchResult(TriggerDispatchOutcome.Accepted, "accepted"));

        var result = await Service(state, authorizer, dispatcher).RunOnceAsync(Request());

        Assert.Equal(TriggerWorkerSelectionStatus.Empty, result.SelectionStatus);
        Assert.Equal(0, authorizer.Calls);
        Assert.Equal(0, dispatcher.Calls);
    }

    [Fact]
    public async Task Authorized_entry_records_intent_before_exactly_one_dispatch_and_terminal_acceptance()
    {
        var state = new WorkerStateStub();
        var authorizer = new AuthorizerStub(Authorized());
        var dispatcher = new DispatcherStub(new TriggerWorkerDispatchResult(TriggerDispatchOutcome.Accepted, "run accepted", Governed()), () => Assert.Equal(1, state.BeginCalls));

        var result = await Service(state, authorizer, dispatcher).RunOnceAsync(Request());

        Assert.Equal(TriggerWorkerMutationStatus.Committed, result.MutationStatus);
        Assert.Equal(1, dispatcher.Calls);
        Assert.Equal(1, state.BeginCalls);
        Assert.Equal(1, state.CompleteCalls);
        Assert.Equal(TriggerDispatchOutcome.Accepted, result.Entry!.Dispatch!.Outcome);
        Assert.Equal(TriggerQueueEntryState.Dispatched, result.Entry.State);
    }

    [Theory]
    [InlineData(TriggerDispatchAuthorizationStatus.Rejected)]
    [InlineData(TriggerDispatchAuthorizationStatus.Unavailable)]
    public async Task Failed_current_evidence_is_durably_rejected_without_dispatch(TriggerDispatchAuthorizationStatus status)
    {
        var state = new WorkerStateStub();
        var authorizer = new AuthorizerStub(new TriggerDispatchAuthorization(status, new string('c', 64), "current evidence rejected"));
        var dispatcher = new DispatcherStub(new TriggerWorkerDispatchResult(TriggerDispatchOutcome.Accepted, "unexpected"));

        var result = await Service(state, authorizer, dispatcher).RunOnceAsync(Request());

        Assert.Equal(0, dispatcher.Calls);
        Assert.Equal(1, state.RejectCalls);
        Assert.Equal(TriggerQueueEntryState.DispatchRejected, result.Entry!.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unavailable_or_malformed_current_evidence_fails_closed_without_dispatch(bool throws)
    {
        var state = new WorkerStateStub();
        var authorizer = throws
            ? new AuthorizerStub(new InvalidOperationException("evidence source unavailable"))
            : new AuthorizerStub(new TriggerDispatchAuthorization((TriggerDispatchAuthorizationStatus)99, "invalid", "malformed evidence"));
        var dispatcher = new DispatcherStub(new TriggerWorkerDispatchResult(TriggerDispatchOutcome.Accepted, "unexpected"));

        var result = await Service(state, authorizer, dispatcher).RunOnceAsync(Request());

        Assert.Equal(0, dispatcher.Calls);
        Assert.Equal(1, state.RejectCalls);
        Assert.Equal(TriggerQueueEntryState.DispatchRejected, result.Entry!.State);
        Assert.Equal(new string('0', 64), result.Entry.Dispatch!.AuthorityEvidenceHash);
        Assert.Contains(throws ? "unavailable" : "malformed", result.Entry.Dispatch.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancellation_before_intent_releases_ownership_without_dispatch()
    {
        var state = new WorkerStateStub();
        var authorizer = new AuthorizerStub(new OperationCanceledException());
        var dispatcher = new DispatcherStub(new TriggerWorkerDispatchResult(TriggerDispatchOutcome.Accepted, "unexpected"));

        var result = await Service(state, authorizer, dispatcher).RunOnceAsync(Request());

        Assert.Equal(1, state.ReleaseCalls);
        Assert.Equal(0, state.BeginCalls);
        Assert.Equal(0, dispatcher.Calls);
        Assert.Equal(TriggerQueueEntryState.Queued, result.Entry!.State);
    }

    [Fact]
    public async Task Cancellation_during_intent_commit_releases_ownership_without_dispatch()
    {
        var state = new WorkerStateStub(beginException: new OperationCanceledException());
        var dispatcher = new DispatcherStub(new TriggerWorkerDispatchResult(TriggerDispatchOutcome.Accepted, "unexpected"));

        var result = await Service(state, new AuthorizerStub(Authorized()), dispatcher).RunOnceAsync(Request());

        Assert.Equal(1, state.BeginCalls);
        Assert.Equal(1, state.ReleaseCalls);
        Assert.Equal(0, dispatcher.Calls);
        Assert.Equal(TriggerQueueEntryState.Queued, result.Entry!.State);
    }

    [Fact]
    public async Task Lost_intent_ownership_does_not_invoke_dispatcher()
    {
        var state = new WorkerStateStub(beginStatus: TriggerWorkerMutationStatus.StaleOwner);
        var dispatcher = new DispatcherStub(new TriggerWorkerDispatchResult(TriggerDispatchOutcome.Accepted, "unexpected"));

        var result = await Service(state, new AuthorizerStub(Authorized()), dispatcher).RunOnceAsync(Request());

        Assert.Equal(TriggerWorkerMutationStatus.StaleOwner, result.MutationStatus);
        Assert.Equal(0, dispatcher.Calls);
        Assert.Equal(0, state.CompleteCalls);
    }

    [Fact]
    public async Task Provider_exception_after_intent_becomes_needs_review()
    {
        var state = new WorkerStateStub();
        var dispatcher = new DispatcherStub(new TimeoutException());

        var result = await Service(state, new AuthorizerStub(Authorized()), dispatcher).RunOnceAsync(Request());

        Assert.Equal(1, state.BeginCalls);
        Assert.Equal(TriggerQueueEntryState.NeedsReview, result.Entry!.State);
        Assert.Equal(TriggerDispatchOutcome.NeedsReview, result.Entry.Dispatch!.Outcome);
    }

    [Fact]
    public async Task Caller_cancellation_after_intent_is_not_forwarded_as_proof_of_non_dispatch()
    {
        using var cancellation = new CancellationTokenSource();
        var state = new WorkerStateStub(onBegin: cancellation.Cancel);
        var dispatcher = new DispatcherStub(new TriggerWorkerDispatchResult(TriggerDispatchOutcome.Accepted, "accepted", Governed()));

        var result = await Service(state, new AuthorizerStub(Authorized()), dispatcher).RunOnceAsync(Request(), cancellation.Token);

        Assert.False(dispatcher.ObservedCancellation);
        Assert.Equal(TriggerQueueEntryState.Dispatched, result.Entry!.State);
    }

    [Fact]
    public async Task Malformed_dispatch_result_after_intent_becomes_needs_review()
    {
        var state = new WorkerStateStub();
        var dispatcher = new DispatcherStub(new TriggerWorkerDispatchResult(TriggerDispatchOutcome.IntentRecorded, "invalid"));

        var result = await Service(state, new AuthorizerStub(Authorized()), dispatcher).RunOnceAsync(Request());

        Assert.Equal(TriggerDispatchOutcome.NeedsReview, result.Entry!.Dispatch!.Outcome);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("operation")]
    [InlineData("definition")]
    public async Task Accepted_result_without_exact_governed_receipt_binding_becomes_needs_review(string mismatch)
    {
        var state = new WorkerStateStub();
        var governed = mismatch switch
        {
            "missing" => null,
            "operation" => Governed() with { OperationId = "other-operation" },
            _ => Governed() with { DefinitionHash = new string('f', 64) }
        };
        var dispatcher = new DispatcherStub(new TriggerWorkerDispatchResult(TriggerDispatchOutcome.Accepted, "fabricated or stale", governed));

        var result = await Service(state, new AuthorizerStub(Authorized()), dispatcher).RunOnceAsync(Request());

        Assert.Equal(TriggerDispatchOutcome.NeedsReview, result.Entry!.Dispatch!.Outcome);
        Assert.Null(result.Entry.Dispatch.GovernedInvocation);
    }

    [Theory]
    [InlineData(TriggerDispatchOutcome.Rejected, TriggerQueueEntryState.DispatchRejected)]
    [InlineData(TriggerDispatchOutcome.Terminal, TriggerQueueEntryState.Dispatched)]
    public async Task Proved_governed_outcome_is_persisted_without_ambiguity(TriggerDispatchOutcome outcome, TriggerQueueEntryState expectedState)
    {
        var state = new WorkerStateStub();
        var governed = outcome == TriggerDispatchOutcome.Terminal ? Governed() : null;
        var dispatcher = new DispatcherStub(new TriggerWorkerDispatchResult(outcome, "proved governed outcome", governed));

        var result = await Service(state, new AuthorizerStub(Authorized()), dispatcher).RunOnceAsync(Request());

        Assert.Equal(expectedState, result.Entry!.State);
        Assert.Equal(outcome, result.Entry.Dispatch!.Outcome);
        Assert.Equal(1, dispatcher.Calls);
    }

    [Fact]
    public void Constructor_rejects_missing_composition_ports()
    {
        var state = new WorkerStateStub();
        var authorizer = new AuthorizerStub(Authorized());
        var dispatcher = new DispatcherStub(new TriggerWorkerDispatchResult(TriggerDispatchOutcome.Accepted, "accepted"));

        Assert.Throws<ArgumentNullException>(() => new TriggerWorkerService(null!, authorizer, dispatcher));
        Assert.Throws<ArgumentNullException>(() => new TriggerWorkerService(state, null!, dispatcher));
        Assert.Throws<ArgumentNullException>(() => new TriggerWorkerService(state, authorizer, null!));
    }

    [Fact]
    public void Governed_operation_identity_is_deterministic_bounded_and_generation_scoped()
    {
        var deliveryId = TriggerAdmissionTestData.Envelope().DeliveryId;

        var first = TriggerWorkerRequestHash.ComputeOperationId(deliveryId, 1);
        var replay = TriggerWorkerRequestHash.ComputeOperationId(deliveryId, 1);
        var next = TriggerWorkerRequestHash.ComputeOperationId(deliveryId, 2);

        Assert.Equal(first, replay);
        Assert.NotEqual(first, next);
        Assert.StartsWith("trigger-", first, StringComparison.Ordinal);
        Assert.True(first.Length <= 120);
        Assert.Throws<ArgumentOutOfRangeException>(() => TriggerWorkerRequestHash.ComputeOperationId(deliveryId, 0));
        Assert.Throws<ArgumentNullException>(() => TriggerWorkerRequestHash.ComputeOperationId(null!, 1));
    }

    private static TriggerWorkerService Service(WorkerStateStub state, AuthorizerStub authorizer, DispatcherStub dispatcher)
    {
        return new TriggerWorkerService(state, authorizer, dispatcher, new FixedTimeProvider(_now));
    }

    private static TriggerWorkerRunRequest Request()
    {
        return new TriggerWorkerRunRequest(new TriggerWorkerSelectionRequest("worker-1", 1, _now, TimeSpan.FromMinutes(1), [], 2));
    }

    private static TriggerDispatchAuthorization Authorized()
    {
        return new TriggerDispatchAuthorization(TriggerDispatchAuthorizationStatus.Authorized, new string('a', 64), "current evidence matched");
    }

    private static TriggerGovernedInvocationEvidence Governed()
    {
        var envelope = TriggerAdmissionTestData.Envelope();
        return new TriggerGovernedInvocationEvidence(TriggerWorkerRequestHash.ComputeOperationId(envelope.DeliveryId, 1), "run-1", new string('d', 64), envelope.Loop.LoopId, envelope.Loop.DefinitionVersion, envelope.Loop.ContentHash);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class AuthorizerStub : ITriggerDispatchAuthorizer
    {
        private readonly TriggerDispatchAuthorization? _result;
        private readonly Exception? _exception;

        internal AuthorizerStub(TriggerDispatchAuthorization result) => _result = result;

        internal AuthorizerStub(Exception exception) => _exception = exception;

        internal int Calls { get; private set; }

        public Task<TriggerDispatchAuthorization> AuthorizeAsync(TriggerDeliveryEnvelope envelope, DateTimeOffset evaluatedAtUtc, CancellationToken cancellationToken = default)
        {
            Calls++;
            return _exception is null ? Task.FromResult(_result!) : Task.FromException<TriggerDispatchAuthorization>(_exception);
        }
    }

    private sealed class DispatcherStub : ITriggerWorkerDispatcher
    {
        private readonly TriggerWorkerDispatchResult? _result;
        private readonly Exception? _exception;
        private readonly Action? _onDispatch;

        internal DispatcherStub(TriggerWorkerDispatchResult result, Action? onDispatch = null)
        {
            _result = result;
            _onDispatch = onDispatch;
        }

        internal DispatcherStub(Exception exception) => _exception = exception;

        internal int Calls { get; private set; }

        internal bool ObservedCancellation { get; private set; }

        public Task<TriggerWorkerDispatchResult> DispatchAsync(TriggerDeliveryEnvelope envelope, TriggerDispatchEvidence intent, CancellationToken cancellationToken = default)
        {
            Calls++;
            ObservedCancellation = cancellationToken.IsCancellationRequested;
            _onDispatch?.Invoke();
            return _exception is null ? Task.FromResult(_result!) : Task.FromException<TriggerWorkerDispatchResult>(_exception);
        }
    }

    private sealed class WorkerStateStub : ITriggerWorkerStatePort
    {
        private readonly TriggerWorkerSelectionStatus _selectionStatus;
        private readonly Action? _onBegin;
        private readonly Exception? _beginException;
        private readonly TriggerWorkerMutationStatus _beginStatus;
        private TriggerQueueEntry _entry;
        private readonly TriggerDeliveryEnvelope _envelope;

        internal WorkerStateStub(TriggerWorkerSelectionStatus selectionStatus = TriggerWorkerSelectionStatus.Acquired, Action? onBegin = null, Exception? beginException = null, TriggerWorkerMutationStatus beginStatus = TriggerWorkerMutationStatus.Committed)
        {
            _selectionStatus = selectionStatus;
            _onBegin = onBegin;
            _beginException = beginException;
            _beginStatus = beginStatus;
            _envelope = TriggerAdmissionTestData.Envelope();
            var lease = new TriggerWorkerLease("worker-1", 1, _now, _now.AddMinutes(1), 0);
            _entry = new TriggerQueueEntry(_envelope.DeliveryId, _envelope.DeduplicationId, _envelope.Loop.LoopId, new string('e', 64), 1, 1, 1, TriggerQueueEntryState.WorkerOwned, TriggerQueueTerminalReason.None, new TriggerQueueOrderKey(_now, TriggerQueuePriority.Normal, _now, _envelope.DeliveryId.Value), 2, _now.AddSeconds(-1), null, TriggerAdmissionStatus.Admitted, TriggerAdmissionReason.EvidenceAccepted, lease);
        }

        internal int BeginCalls { get; private set; }

        internal int CompleteCalls { get; private set; }

        internal int RejectCalls { get; private set; }

        internal int ReleaseCalls { get; private set; }

        public Task<TriggerWorkerSelectionResult> SelectAsync(TriggerWorkerSelectionRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new TriggerWorkerSelectionResult(_selectionStatus, 2, _selectionStatus == TriggerWorkerSelectionStatus.Acquired ? _entry : null, _selectionStatus == TriggerWorkerSelectionStatus.Acquired ? _envelope : null));
        }

        public Task<TriggerWorkerMutationResult> RenewAsync(TriggerDeliveryId deliveryId, string workerId, long leaseGeneration, long expectedRevision, DateTimeOffset renewedAtUtc, TimeSpan leaseDuration, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TriggerWorkerMutationResult> ReleaseAsync(TriggerDeliveryId deliveryId, string workerId, long leaseGeneration, long expectedRevision, DateTimeOffset releasedAtUtc, CancellationToken cancellationToken = default)
        {
            ReleaseCalls++;
            _entry = _entry with { State = TriggerQueueEntryState.Queued, Revision = _entry.Revision + 1, WorkerLease = _entry.WorkerLease! with { ReleasedAtUtc = releasedAtUtc } };
            return Result();
        }

        public Task<TriggerWorkerMutationResult> BeginDispatchAsync(TriggerDeliveryId deliveryId, string workerId, long leaseGeneration, long expectedRevision, TriggerDispatchEvidence intent, CancellationToken cancellationToken = default)
        {
            BeginCalls++;
            if (_beginException is not null)
            {
                return Task.FromException<TriggerWorkerMutationResult>(_beginException);
            }

            if (_beginStatus != TriggerWorkerMutationStatus.Committed)
            {
                return Task.FromResult(new TriggerWorkerMutationResult(_beginStatus, 2, _entry));
            }

            _entry = _entry with { State = TriggerQueueEntryState.Dispatching, Revision = _entry.Revision + 1, Dispatch = intent };
            _onBegin?.Invoke();
            return Result();
        }

        public Task<TriggerWorkerMutationResult> RejectBeforeDispatchAsync(TriggerDeliveryId deliveryId, string workerId, long leaseGeneration, long expectedRevision, TriggerDispatchEvidence rejection, CancellationToken cancellationToken = default)
        {
            RejectCalls++;
            _entry = _entry with { State = TriggerQueueEntryState.DispatchRejected, Revision = _entry.Revision + 1, TerminalReason = TriggerQueueTerminalReason.DispatchRejected, TerminalAtUtc = rejection.OutcomeRecordedAtUtc, Dispatch = rejection };
            return Result();
        }

        public Task<TriggerWorkerMutationResult> CompleteDispatchAsync(TriggerDeliveryId deliveryId, string workerId, long leaseGeneration, long expectedRevision, TriggerDispatchEvidence outcome, CancellationToken cancellationToken = default)
        {
            CompleteCalls++;
            var state = outcome.Outcome is TriggerDispatchOutcome.Accepted or TriggerDispatchOutcome.Terminal ? TriggerQueueEntryState.Dispatched : outcome.Outcome == TriggerDispatchOutcome.Rejected ? TriggerQueueEntryState.DispatchRejected : TriggerQueueEntryState.NeedsReview;
            var reason = state == TriggerQueueEntryState.Dispatched ? TriggerQueueTerminalReason.Dispatched : state == TriggerQueueEntryState.DispatchRejected ? TriggerQueueTerminalReason.DispatchRejected : TriggerQueueTerminalReason.AmbiguousDispatch;
            _entry = _entry with { State = state, Revision = _entry.Revision + 1, TerminalReason = reason, TerminalAtUtc = outcome.OutcomeRecordedAtUtc, Dispatch = outcome };
            return Result();
        }

        private Task<TriggerWorkerMutationResult> Result() => Task.FromResult(new TriggerWorkerMutationResult(TriggerWorkerMutationStatus.Committed, 3, _entry));
    }
}
