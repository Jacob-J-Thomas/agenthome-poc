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
    public async Task Pending_schedule_finalization_releases_ownership_before_authority_intent_or_dispatch()
    {
        var state = new WorkerStateStub();
        var authorizer = new AuthorizerStub(Authorized());
        var dispatcher = new DispatcherStub(new TriggerWorkerDispatchResult(TriggerDispatchOutcome.Accepted, "unexpected"));
        var readiness = new ReadinessStub(TriggerWorkerDispatchReadinessStatus.RetryAfterScheduleFinalization);

        var result = await Service(state, authorizer, dispatcher, readiness: readiness).RunOnceAsync(Request());

        Assert.Equal(TriggerWorkerSelectionStatus.Acquired, result.SelectionStatus);
        Assert.Equal(TriggerWorkerMutationStatus.Committed, result.MutationStatus);
        Assert.Equal(TriggerQueueEntryState.Queued, result.Entry!.State);
        Assert.Equal(1, readiness.Calls);
        Assert.Equal(1, state.ReleaseCalls);
        Assert.Equal(0, authorizer.Calls);
        Assert.Equal(0, state.BeginCalls);
        Assert.Equal(0, dispatcher.Calls);
        Assert.Null(result.Entry.Dispatch);
    }

    [Theory]
    [InlineData(TriggerWorkerDispatchReadinessStatus.RequiresAttention)]
    [InlineData(TriggerWorkerDispatchReadinessStatus.Unknown)]
    public async Task Unproved_readiness_records_bounded_attention_without_release_or_provider(
        TriggerWorkerDispatchReadinessStatus status)
    {
        var state = new WorkerStateStub();
        var authorizer = new AuthorizerStub(Authorized());
        var dispatcher = new DispatcherStub(new TriggerWorkerDispatchResult(TriggerDispatchOutcome.Accepted, "unexpected"));

        var result = await Service(
            state,
            authorizer,
            dispatcher,
            readiness: new ReadinessStub(status)).RunOnceAsync(Request());

        Assert.Equal(0, state.ReleaseCalls);
        Assert.Equal(1, authorizer.Calls);
        Assert.Equal(1, state.BeginCalls);
        Assert.Equal(0, dispatcher.Calls);
        Assert.Equal(1, state.CompleteCalls);
        Assert.Equal(TriggerQueueEntryState.NeedsReview, result.Entry!.State);
        Assert.Equal(TriggerDispatchOutcome.NeedsReview, result.Entry.Dispatch!.Outcome);
        Assert.Contains("readiness", result.Entry.Dispatch.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Readiness_exception_records_bounded_attention_without_release_or_provider()
    {
        var state = new WorkerStateStub();
        var authorizer = new AuthorizerStub(Authorized());
        var dispatcher = new DispatcherStub(new TriggerWorkerDispatchResult(TriggerDispatchOutcome.Accepted, "unexpected"));

        var result = await Service(
            state,
            authorizer,
            dispatcher,
            readiness: new ReadinessStub(new IOException("schedule evidence unavailable"))).RunOnceAsync(Request());

        Assert.Equal(0, state.ReleaseCalls);
        Assert.Equal(1, authorizer.Calls);
        Assert.Equal(1, state.BeginCalls);
        Assert.Equal(0, dispatcher.Calls);
        Assert.Equal(1, state.CompleteCalls);
        Assert.Equal(TriggerQueueEntryState.NeedsReview, result.Entry!.State);
    }

    [Fact]
    public async Task Missing_readiness_result_records_bounded_attention_without_release_or_provider()
    {
        var state = new WorkerStateStub();
        var dispatcher = new DispatcherStub(new TriggerWorkerDispatchResult(TriggerDispatchOutcome.Accepted, "unexpected"));

        var result = await Service(
            state,
            new AuthorizerStub(Authorized()),
            dispatcher,
            readiness: new ReadinessStub()).RunOnceAsync(Request());

        Assert.Equal(0, state.ReleaseCalls);
        Assert.Equal(1, state.BeginCalls);
        Assert.Equal(0, dispatcher.Calls);
        Assert.Equal(TriggerQueueEntryState.NeedsReview, result.Entry!.State);
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
    public async Task Terminal_completion_exception_retries_once_as_needs_review_without_redispatch()
    {
        var state = new WorkerStateStub(completeException: new IOException("completion unavailable"));
        var dispatcher = new DispatcherStub(new TriggerWorkerDispatchResult(TriggerDispatchOutcome.Accepted, "accepted before completion failure", Governed()));

        var result = await Service(state, new AuthorizerStub(Authorized()), dispatcher).RunOnceAsync(Request());

        Assert.Equal(1, dispatcher.Calls);
        Assert.Equal(2, state.CompleteCalls);
        Assert.Equal(TriggerWorkerMutationStatus.Committed, result.MutationStatus);
        Assert.Equal(TriggerQueueEntryState.NeedsReview, result.Entry!.State);
        Assert.Equal(TriggerDispatchOutcome.NeedsReview, result.Entry.Dispatch!.Outcome);
        Assert.Null(result.Entry.Dispatch.GovernedInvocation);
        Assert.Contains("IOException", result.Entry.Dispatch.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repeated_terminal_completion_exceptions_return_unavailable_known_intent_without_redispatch()
    {
        var state = new WorkerStateStub(completeException: new IOException("completion unavailable"), completeExceptionCount: 2);
        var dispatcher = new DispatcherStub(new TriggerWorkerDispatchResult(TriggerDispatchOutcome.Accepted, "accepted before completion failure", Governed()));

        var result = await Service(state, new AuthorizerStub(Authorized()), dispatcher).RunOnceAsync(Request());

        Assert.Equal(1, dispatcher.Calls);
        Assert.Equal(2, state.CompleteCalls);
        Assert.Equal(TriggerWorkerMutationStatus.Unavailable, result.MutationStatus);
        Assert.Equal(TriggerQueueEntryState.Dispatching, result.Entry!.State);
        Assert.Equal(TriggerDispatchOutcome.IntentRecorded, result.Entry.Dispatch!.Outcome);
    }

    [Fact]
    public async Task Noncommitted_completion_with_exact_current_intent_retries_as_needs_review()
    {
        var state = new WorkerStateStub(firstCompleteStatus: TriggerWorkerMutationStatus.RevisionConflict);
        var dispatcher = new DispatcherStub(new TriggerWorkerDispatchResult(
            TriggerDispatchOutcome.Accepted,
            "accepted before optimistic completion conflict",
            Governed()));

        var result = await Service(state, new AuthorizerStub(Authorized()), dispatcher).RunOnceAsync(Request());

        Assert.Equal(1, dispatcher.Calls);
        Assert.Equal(2, state.CompleteCalls);
        Assert.Equal(TriggerWorkerMutationStatus.Committed, result.MutationStatus);
        Assert.Equal(TriggerQueueEntryState.NeedsReview, result.Entry!.State);
        Assert.Contains("RevisionConflict", result.Entry.Dispatch!.Detail, StringComparison.Ordinal);
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
    public async Task Long_dispatch_renews_exact_ownership_multiple_times_and_stops_before_completion()
    {
        var time = new ControlledTimeProvider(_now);
        var state = new WorkerStateStub();
        var dispatchCompletion = new TaskCompletionSource<TriggerWorkerDispatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new DispatcherStub(_ => dispatchCompletion.Task);
        var running = Service(state, new AuthorizerStub(Authorized()), dispatcher, time).RunOnceAsync(Request());
        await state.WaitForBeginAsync();

        time.Advance(TimeSpan.FromSeconds(31));
        await state.WaitForRenewalsAsync(1);
        await time.WaitForTimerCreationsAsync(2);
        time.Advance(TimeSpan.FromSeconds(31));
        await state.WaitForRenewalsAsync(2);
        dispatchCompletion.SetResult(new TriggerWorkerDispatchResult(TriggerDispatchOutcome.Accepted, "accepted after renewal", Governed()));

        var result = await running;
        var renewalsAtCompletion = state.RenewCalls;
        time.Advance(TimeSpan.FromMinutes(2));
        await Task.Yield();

        Assert.Equal(2, renewalsAtCompletion);
        Assert.Equal(renewalsAtCompletion, state.RenewCalls);
        Assert.Equal(5, state.CompletionExpectedRevision);
        Assert.Equal(2, result.Entry!.WorkerLease!.RenewalCount);
        Assert.Equal(TriggerQueueEntryState.Dispatched, result.Entry.State);
    }

    [Fact]
    public async Task Initial_renewal_uses_remaining_lease_window_after_slow_authorization()
    {
        var time = new ControlledTimeProvider(_now);
        var state = new WorkerStateStub(initialLeaseDuration: TimeSpan.FromSeconds(10));
        var dispatchCompletion = new TaskCompletionSource<TriggerWorkerDispatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new DispatcherStub(_ => dispatchCompletion.Task);
        var running = Service(state, new AuthorizerStub(Authorized()), dispatcher, time).RunOnceAsync(Request());
        await state.WaitForBeginAsync();

        time.Advance(TimeSpan.FromSeconds(6));
        await state.WaitForRenewalsAsync(1);
        dispatchCompletion.SetResult(new TriggerWorkerDispatchResult(TriggerDispatchOutcome.Accepted, "accepted after shortened first renewal", Governed()));
        var result = await running;

        Assert.Equal(1, result.Entry!.WorkerLease!.RenewalCount);
        Assert.Equal(_now.AddSeconds(66), result.Entry.WorkerLease.ExpiresAtUtc);
        Assert.Equal(TriggerQueueEntryState.Dispatched, result.Entry.State);
    }

    [Theory]
    [InlineData(TriggerWorkerMutationStatus.RevisionConflict)]
    [InlineData(TriggerWorkerMutationStatus.StaleOwner)]
    public async Task Renewal_ownership_loss_cancels_active_invocation_and_cannot_overwrite_swept_posture(TriggerWorkerMutationStatus renewalStatus)
    {
        var time = new ControlledTimeProvider(_now);
        var state = new WorkerStateStub(renewStatus: renewalStatus, terminalizeOnRenewLoss: true);
        var dispatcher = new DispatcherStub(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new TriggerWorkerDispatchResult(TriggerDispatchOutcome.Accepted, "unreachable", Governed());
        });
        var running = Service(state, new AuthorizerStub(Authorized()), dispatcher, time).RunOnceAsync(Request());
        await state.WaitForBeginAsync();

        time.Advance(TimeSpan.FromSeconds(31));
        var result = await running;

        Assert.True(dispatcher.ObservedCancellation);
        Assert.Equal(1, state.RenewCalls);
        Assert.Equal(1, state.CompleteCalls);
        Assert.Equal(TriggerWorkerMutationStatus.InvalidState, result.MutationStatus);
        Assert.Equal(TriggerQueueEntryState.NeedsReview, result.Entry!.State);
        Assert.Equal(TriggerDispatchOutcome.NeedsReview, result.Entry.Dispatch!.Outcome);
        Assert.Null(result.Entry.Dispatch.GovernedInvocation);
    }

    [Fact]
    public async Task Renewal_failure_cancels_active_invocation_and_records_needs_review_against_last_exact_revision()
    {
        var time = new ControlledTimeProvider(_now);
        var state = new WorkerStateStub(renewException: new IOException("renewal unavailable"));
        var dispatcher = new DispatcherStub(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new TriggerWorkerDispatchResult(TriggerDispatchOutcome.Accepted, "unreachable", Governed());
        });
        var running = Service(state, new AuthorizerStub(Authorized()), dispatcher, time).RunOnceAsync(Request());
        await state.WaitForBeginAsync();

        time.Advance(TimeSpan.FromSeconds(31));
        var result = await running;

        Assert.True(dispatcher.ObservedCancellation);
        Assert.Equal(TriggerWorkerMutationStatus.Committed, result.MutationStatus);
        Assert.Equal(TriggerQueueEntryState.NeedsReview, result.Entry!.State);
        Assert.Equal(TriggerDispatchOutcome.NeedsReview, result.Entry.Dispatch!.Outcome);
        Assert.Contains("renewal failed closed", result.Entry.Dispatch.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Dispatch_completion_cancels_a_renewal_already_inside_the_state_boundary()
    {
        var time = new ControlledTimeProvider(_now);
        var state = new WorkerStateStub(blockRenewUntilCancellation: true);
        var dispatchCompletion = new TaskCompletionSource<TriggerWorkerDispatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new DispatcherStub(_ => dispatchCompletion.Task);
        var running = Service(state, new AuthorizerStub(Authorized()), dispatcher, time).RunOnceAsync(Request());
        await state.WaitForBeginAsync();

        time.Advance(TimeSpan.FromSeconds(31));
        await state.WaitForRenewalsAsync(1);
        dispatchCompletion.SetResult(new TriggerWorkerDispatchResult(
            TriggerDispatchOutcome.Accepted,
            "accepted while renewal was pending",
            Governed()));

        var result = await running;

        Assert.Equal(1, state.RenewCalls);
        Assert.Equal(TriggerWorkerMutationStatus.Committed, result.MutationStatus);
        Assert.Equal(TriggerQueueEntryState.Dispatched, result.Entry!.State);
        Assert.Equal(TriggerDispatchOutcome.Accepted, result.Entry.Dispatch!.Outcome);
    }

    [Fact]
    public async Task Malformed_overflowing_renewal_coordinates_fail_closed_before_dispatch_completion()
    {
        var time = new ControlledTimeProvider(_now);
        var state = new WorkerStateStub(overflowRenewalValidation: true);
        var dispatcher = new DispatcherStub(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new TriggerWorkerDispatchResult(TriggerDispatchOutcome.Accepted, "unreachable", Governed());
        });
        var running = Service(state, new AuthorizerStub(Authorized()), dispatcher, time).RunOnceAsync(Request());
        await state.WaitForBeginAsync();

        time.Advance(TimeSpan.FromSeconds(31));
        var result = await running;

        Assert.True(dispatcher.ObservedCancellation);
        Assert.Equal(1, state.RenewCalls);
        Assert.Equal(TriggerWorkerMutationStatus.Committed, result.MutationStatus);
        Assert.Equal(TriggerQueueEntryState.NeedsReview, result.Entry!.State);
        Assert.Equal(TriggerDispatchOutcome.NeedsReview, result.Entry.Dispatch!.Outcome);
        Assert.Contains("renewal validation failed closed", result.Entry.Dispatch.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Expired_ownership_immediately_after_intent_never_starts_governed_dispatch()
    {
        var time = new ControlledTimeProvider(_now);
        var state = new WorkerStateStub(onBegin: () => time.Advance(TimeSpan.FromMinutes(1)));
        var dispatcher = new DispatcherStub(new TriggerWorkerDispatchResult(TriggerDispatchOutcome.Accepted, "must not dispatch", Governed()));

        var result = await Service(state, new AuthorizerStub(Authorized()), dispatcher, time).RunOnceAsync(Request());

        Assert.Equal(0, dispatcher.Calls);
        Assert.Equal(0, state.RenewCalls);
        Assert.Equal(TriggerDispatchOutcome.NeedsReview, result.Entry!.Dispatch!.Outcome);
        Assert.Contains("expired", result.Entry.Dispatch.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Renewal_clock_failure_cancels_active_invocation_and_completes_needs_review_with_last_trusted_time()
    {
        var time = new ControlledTimeProvider(_now);
        var state = new WorkerStateStub();
        var dispatcher = new DispatcherStub(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new TriggerWorkerDispatchResult(TriggerDispatchOutcome.Accepted, "unreachable", Governed());
        });
        var running = Service(state, new AuthorizerStub(Authorized()), dispatcher, time).RunOnceAsync(Request());
        await state.WaitForBeginAsync();
        await time.WaitForTimerCreationsAsync(1);

        time.UtcNowException = new InvalidOperationException("clock unavailable");
        time.Advance(TimeSpan.FromSeconds(31));
        var result = await running;

        Assert.True(dispatcher.ObservedCancellation);
        Assert.Equal(0, state.RenewCalls);
        Assert.Equal(TriggerQueueEntryState.NeedsReview, result.Entry!.State);
        Assert.Equal(_now, result.Entry.Dispatch!.OutcomeRecordedAtUtc);
        Assert.Contains("clock failed closed", result.Entry.Dispatch.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ownership_expiring_while_dispatch_settles_is_durably_needs_review()
    {
        var time = new ControlledTimeProvider(_now);
        var state = new WorkerStateStub();
        var dispatcher = new DispatcherStub(new TriggerWorkerDispatchResult(
            TriggerDispatchOutcome.Accepted,
            "accepted after ownership expiry",
            Governed()),
            onDispatch: () => time.AdvanceClockWithoutFiringTimers(TimeSpan.FromMinutes(2)));

        var result = await Service(state, new AuthorizerStub(Authorized()), dispatcher, time).RunOnceAsync(Request());

        Assert.Equal(1, dispatcher.Calls);
        Assert.Equal(TriggerQueueEntryState.NeedsReview, result.Entry!.State);
        Assert.Equal(TriggerDispatchOutcome.NeedsReview, result.Entry.Dispatch!.Outcome);
        Assert.Contains("ownership expired", result.Entry.Dispatch.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Completion_retry_uses_the_later_trusted_clock_without_redispatch()
    {
        var time = new ControlledTimeProvider(_now);
        var state = new WorkerStateStub(
            completeException: new IOException("completion unavailable"),
            onComplete: (attempt, _) =>
            {
                if (attempt == 1)
                {
                    time.Advance(TimeSpan.FromSeconds(1));
                }
            });
        var dispatcher = new DispatcherStub(new TriggerWorkerDispatchResult(
            TriggerDispatchOutcome.Accepted,
            "accepted before completion failure",
            Governed()));

        var result = await Service(state, new AuthorizerStub(Authorized()), dispatcher, time).RunOnceAsync(Request());

        Assert.Equal(1, dispatcher.Calls);
        Assert.Equal(2, state.CompleteCalls);
        Assert.Equal(TriggerDispatchOutcome.Accepted, state.CompletionAttempts[0].Outcome);
        Assert.Equal(_now, state.CompletionAttempts[0].OutcomeRecordedAtUtc);
        Assert.Equal(TriggerDispatchOutcome.NeedsReview, state.CompletionAttempts[1].Outcome);
        Assert.Equal(_now.AddSeconds(1), state.CompletionAttempts[1].OutcomeRecordedAtUtc);
        Assert.Equal(_now.AddSeconds(1), result.Entry!.Dispatch!.OutcomeRecordedAtUtc);
        Assert.Equal(TriggerDispatchOutcome.NeedsReview, result.Entry.Dispatch.Outcome);
    }

    [Fact]
    public async Task Completion_retry_clock_failure_is_bounded_and_preserves_the_known_intent_time()
    {
        var state = new WorkerStateStub(completeException: new IOException("completion unavailable"));
        var time = new ThrowOnCallTimeProvider(_now, throwOnCall: 6);
        var dispatcher = new DispatcherStub(new TriggerWorkerDispatchResult(
            TriggerDispatchOutcome.Accepted,
            "accepted before completion failure",
            Governed()));

        var result = await Service(state, new AuthorizerStub(Authorized()), dispatcher, time).RunOnceAsync(Request());

        Assert.Equal(1, dispatcher.Calls);
        Assert.Equal(2, state.CompleteCalls);
        Assert.Equal(_now, result.Entry!.Dispatch!.OutcomeRecordedAtUtc);
        Assert.Contains("retry clock IOException", result.Entry.Dispatch.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Renewal_timer_failure_prevents_dispatch_and_records_attention()
    {
        var state = new WorkerStateStub();
        var dispatcher = new DispatcherStub(new TriggerWorkerDispatchResult(
            TriggerDispatchOutcome.Accepted,
            "must not dispatch",
            Governed()));

        var result = await Service(
            state,
            new AuthorizerStub(Authorized()),
            dispatcher,
            new ThrowingTimerTimeProvider(_now)).RunOnceAsync(Request());

        Assert.Equal(0, dispatcher.Calls);
        Assert.Equal(TriggerQueueEntryState.NeedsReview, result.Entry!.State);
        Assert.Contains("renewal scheduling failed closed", result.Entry.Dispatch!.Detail, StringComparison.OrdinalIgnoreCase);
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
    [InlineData("run")]
    [InlineData("admission")]
    [InlineData("loop")]
    [InlineData("reference")]
    public async Task Accepted_result_without_exact_governed_receipt_binding_becomes_needs_review(string mismatch)
    {
        var state = new WorkerStateStub();
        var governed = mismatch switch
        {
            "missing" => null,
            "operation" => Governed() with { OperationId = "other-operation" },
            "run" => Governed() with { RunId = "../run" },
            "admission" => Governed() with { AdmissionRequestHash = new string('F', 64) },
            "loop" => Governed() with { LoopId = "other-loop" },
            _ => Governed() with { LoopReferenceHash = new string('f', 64) }
        };
        var dispatcher = new DispatcherStub(new TriggerWorkerDispatchResult(TriggerDispatchOutcome.Accepted, "fabricated or stale", governed));

        var result = await Service(state, new AuthorizerStub(Authorized()), dispatcher).RunOnceAsync(Request());

        Assert.Equal(TriggerDispatchOutcome.NeedsReview, result.Entry!.Dispatch!.Outcome);
        Assert.Null(result.Entry.Dispatch.GovernedInvocation);
    }

    [Fact]
    public async Task Canonical_governed_target_is_accepted_only_with_its_full_reference_hash()
    {
        var envelope = TriggerAdmissionTestData.Envelope(loop: TriggerAdmissionTestData.GovernedLoop());
        var state = new WorkerStateStub(envelope: envelope);
        var dispatcher = new DispatcherStub(new TriggerWorkerDispatchResult(TriggerDispatchOutcome.Accepted, "canonical governed target accepted", Governed(envelope)));

        var result = await Service(state, new AuthorizerStub(Authorized()), dispatcher).RunOnceAsync(Request());

        Assert.Equal(TriggerQueueEntryState.Dispatched, result.Entry!.State);
        Assert.Equal(envelope.Loop.LoopId, result.Entry.Dispatch!.GovernedInvocation!.LoopId);
        Assert.True(TriggerLoopReferenceHash.TryCompute(envelope.Loop, out var expectedHash, out _));
        Assert.Equal(expectedHash, result.Entry.Dispatch.GovernedInvocation.LoopReferenceHash);
        Assert.Contains(typeof(TriggerGovernedInvocationEvidence).GetProperties(), property => property.Name == nameof(TriggerGovernedInvocationEvidence.LoopReferenceHash));
        Assert.DoesNotContain(typeof(TriggerGovernedInvocationEvidence).GetProperties(), property => property.Name.Contains("Definition", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("grant")]
    public async Task Canonical_target_hash_rejects_same_graph_with_different_exact_pin(string mismatch)
    {
        var envelope = TriggerAdmissionTestData.Envelope(loop: TriggerAdmissionTestData.GovernedLoop());
        var otherLoop = mismatch == "revision"
            ? TriggerAdmissionTestData.GovernedLoop(revisionId: "revision-4")
            : TriggerAdmissionTestData.GovernedLoop(grantRevision: 3);
        var forgedEnvelope = TriggerAdmissionTestData.Envelope(loop: otherLoop);
        var state = new WorkerStateStub(envelope: envelope);
        var dispatcher = new DispatcherStub(new TriggerWorkerDispatchResult(TriggerDispatchOutcome.Accepted, "same graph but stale exact pin", Governed(forgedEnvelope)));

        var result = await Service(state, new AuthorizerStub(Authorized()), dispatcher).RunOnceAsync(Request());

        Assert.Equal(envelope.Loop.LoopId, forgedEnvelope.Loop.LoopId);
        Assert.Equal(TriggerQueueEntryState.NeedsReview, result.Entry!.State);
        Assert.Null(result.Entry.Dispatch!.GovernedInvocation);
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

        var readiness = new ReadinessStub(TriggerWorkerDispatchReadinessStatus.Ready);
        Assert.Throws<ArgumentNullException>(() => new TriggerWorkerService(null!, authorizer, dispatcher, readiness));
        Assert.Throws<ArgumentNullException>(() => new TriggerWorkerService(state, null!, dispatcher, readiness));
        Assert.Throws<ArgumentNullException>(() => new TriggerWorkerService(state, authorizer, null!, readiness));
        Assert.Throws<ArgumentNullException>(() => new TriggerWorkerService(state, authorizer, dispatcher, null!));
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
        Assert.True(TriggerDispatchOperationId.IsValid(first));
        Assert.True(first.Length <= 120);
        Assert.Throws<ArgumentOutOfRangeException>(() => TriggerWorkerRequestHash.ComputeOperationId(deliveryId, 0));
        Assert.Throws<ArgumentNullException>(() => TriggerWorkerRequestHash.ComputeOperationId(null!, 1));
    }

    private static TriggerWorkerService Service(
        WorkerStateStub state,
        AuthorizerStub authorizer,
        DispatcherStub dispatcher,
        TimeProvider? timeProvider = null,
        ITriggerWorkerDispatchReadinessPort? readiness = null)
    {
        return new TriggerWorkerService(
            state,
            authorizer,
            dispatcher,
            readiness ?? new ReadinessStub(TriggerWorkerDispatchReadinessStatus.Ready),
            timeProvider ?? new FixedTimeProvider(_now));
    }

    private static TriggerWorkerRunRequest Request()
    {
        return new TriggerWorkerRunRequest(new TriggerWorkerSelectionRequest("worker-1", 1, _now, TimeSpan.FromMinutes(1), [], 2));
    }

    private static TriggerDispatchAuthorization Authorized()
    {
        return new TriggerDispatchAuthorization(TriggerDispatchAuthorizationStatus.Authorized, new string('a', 64), "current evidence matched");
    }

    private static TriggerGovernedInvocationEvidence Governed(TriggerDeliveryEnvelope? envelope = null)
    {
        envelope ??= TriggerAdmissionTestData.Envelope();
        Assert.True(TriggerLoopReferenceHash.TryCompute(envelope.Loop, out var loopReferenceHash, out _));
        return new TriggerGovernedInvocationEvidence(TriggerWorkerRequestHash.ComputeOperationId(envelope.DeliveryId, 1), "run-1", new string('d', 64), envelope.Loop.LoopId, loopReferenceHash!);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ThrowOnCallTimeProvider(DateTimeOffset now, int throwOnCall) : TimeProvider
    {
        private int _calls;

        public override DateTimeOffset GetUtcNow()
            => Interlocked.Increment(ref _calls) == throwOnCall
                ? throw new IOException("clock unavailable")
                : now;
    }

    private sealed class ThrowingTimerTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
            => throw new InvalidOperationException("timer unavailable");
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

    private sealed class ReadinessStub : ITriggerWorkerDispatchReadinessPort
    {
        private readonly TriggerWorkerDispatchReadinessStatus _status;
        private readonly Exception? _exception;
        private readonly bool _returnNull;

        internal ReadinessStub() => _returnNull = true;

        internal ReadinessStub(TriggerWorkerDispatchReadinessStatus status) => _status = status;

        internal ReadinessStub(Exception exception) => _exception = exception;

        internal int Calls { get; private set; }

        public Task<TriggerWorkerDispatchReadinessResult> CheckAsync(
            TriggerDeliveryEnvelope envelope,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return _returnNull
                ? Task.FromResult<TriggerWorkerDispatchReadinessResult>(null!)
                : _exception is null
                ? Task.FromResult(new TriggerWorkerDispatchReadinessResult(_status))
                : Task.FromException<TriggerWorkerDispatchReadinessResult>(_exception);
        }
    }

    private sealed class DispatcherStub : ITriggerWorkerDispatcher
    {
        private readonly TriggerWorkerDispatchResult? _result;
        private readonly Exception? _exception;
        private readonly Action? _onDispatch;
        private readonly Func<CancellationToken, Task<TriggerWorkerDispatchResult>>? _dispatch;

        internal DispatcherStub(TriggerWorkerDispatchResult result, Action? onDispatch = null)
        {
            _result = result;
            _onDispatch = onDispatch;
        }

        internal DispatcherStub(Exception exception) => _exception = exception;

        internal DispatcherStub(Func<CancellationToken, Task<TriggerWorkerDispatchResult>> dispatch) => _dispatch = dispatch;

        internal int Calls { get; private set; }

        internal bool ObservedCancellation { get; private set; }

        public async Task<TriggerWorkerDispatchResult> DispatchAsync(TriggerDeliveryEnvelope envelope, TriggerDispatchEvidence intent, CancellationToken cancellationToken = default)
        {
            Calls++;
            ObservedCancellation = cancellationToken.IsCancellationRequested;
            _onDispatch?.Invoke();
            try
            {
                if (_dispatch is not null)
                {
                    return await _dispatch(cancellationToken);
                }

                return _exception is null ? _result! : throw _exception;
            }
            finally
            {
                ObservedCancellation |= cancellationToken.IsCancellationRequested;
            }
        }
    }

    private sealed class WorkerStateStub : ITriggerWorkerStatePort
    {
        private readonly TriggerWorkerSelectionStatus _selectionStatus;
        private readonly Action? _onBegin;
        private readonly Exception? _beginException;
        private readonly TriggerWorkerMutationStatus _beginStatus;
        private readonly TriggerWorkerMutationStatus _renewStatus;
        private readonly Exception? _renewException;
        private readonly bool _terminalizeOnRenewLoss;
        private readonly Exception? _completeException;
        private readonly int _completeExceptionCount;
        private readonly TriggerWorkerMutationStatus? _firstCompleteStatus;
        private readonly Action<int, TriggerDispatchEvidence>? _onComplete;
        private TriggerQueueEntry _entry;
        private readonly TriggerDeliveryEnvelope _envelope;
        private readonly TaskCompletionSource _begun = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly bool _blockRenewUntilCancellation;
        private readonly bool _overflowRenewalValidation;

        internal WorkerStateStub(TriggerWorkerSelectionStatus selectionStatus = TriggerWorkerSelectionStatus.Acquired, Action? onBegin = null, Exception? beginException = null, TriggerWorkerMutationStatus beginStatus = TriggerWorkerMutationStatus.Committed, TriggerWorkerMutationStatus renewStatus = TriggerWorkerMutationStatus.Committed, Exception? renewException = null, bool terminalizeOnRenewLoss = false, TimeSpan? initialLeaseDuration = null, Exception? completeException = null, int completeExceptionCount = 1, TriggerWorkerMutationStatus? firstCompleteStatus = null, TriggerDeliveryEnvelope? envelope = null, Action<int, TriggerDispatchEvidence>? onComplete = null, bool blockRenewUntilCancellation = false, bool overflowRenewalValidation = false)
        {
            _selectionStatus = selectionStatus;
            _onBegin = onBegin;
            _beginException = beginException;
            _beginStatus = beginStatus;
            _renewStatus = renewStatus;
            _renewException = renewException;
            _terminalizeOnRenewLoss = terminalizeOnRenewLoss;
            _completeException = completeException;
            _completeExceptionCount = completeExceptionCount;
            _firstCompleteStatus = firstCompleteStatus;
            _onComplete = onComplete;
            _blockRenewUntilCancellation = blockRenewUntilCancellation;
            _overflowRenewalValidation = overflowRenewalValidation;
            _envelope = envelope ?? TriggerAdmissionTestData.Envelope();
            var lease = new TriggerWorkerLease("worker-1", 1, _now, _now + (initialLeaseDuration ?? TimeSpan.FromMinutes(1)), 0);
            _entry = new TriggerQueueEntry(_envelope.DeliveryId, _envelope.DeduplicationId, _envelope.Loop.LoopId, new string('e', 64), 1, 1, 1, TriggerQueueEntryState.WorkerOwned, TriggerQueueTerminalReason.None, new TriggerQueueOrderKey(_now, TriggerQueuePriority.Normal, _now, _envelope.DeliveryId.Value), 2, _now.AddSeconds(-1), null, TriggerAdmissionStatus.Admitted, TriggerAdmissionReason.EvidenceAccepted, lease);
        }

        internal int BeginCalls { get; private set; }

        internal int CompleteCalls { get; private set; }

        internal List<TriggerDispatchEvidence> CompletionAttempts { get; } = [];

        internal long? CompletionExpectedRevision { get; private set; }

        internal int RejectCalls { get; private set; }

        internal int ReleaseCalls { get; private set; }

        internal int RenewCalls { get; private set; }

        public Task<TriggerWorkerSelectionResult> SelectAsync(TriggerWorkerSelectionRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new TriggerWorkerSelectionResult(_selectionStatus, 2, _selectionStatus == TriggerWorkerSelectionStatus.Acquired ? _entry : null, _selectionStatus == TriggerWorkerSelectionStatus.Acquired ? _envelope : null));
        }

        public Task<TriggerWorkerMutationResult> RenewAsync(TriggerDeliveryId deliveryId, string workerId, long leaseGeneration, long expectedRevision, DateTimeOffset renewedAtUtc, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        {
            RenewCalls++;
            if (_blockRenewUntilCancellation)
            {
                return WaitForCancellationAsync(cancellationToken);
            }

            if (_renewException is not null)
            {
                return Task.FromException<TriggerWorkerMutationResult>(_renewException);
            }

            if (_renewStatus is not (TriggerWorkerMutationStatus.Committed or TriggerWorkerMutationStatus.Replayed))
            {
                if (_terminalizeOnRenewLoss)
                {
                    var dispatch = _entry.Dispatch! with { Outcome = TriggerDispatchOutcome.NeedsReview, OutcomeRecordedAtUtc = renewedAtUtc, Detail = "Concurrent sweep retained ambiguous dispatch evidence." };
                    _entry = _entry with { State = TriggerQueueEntryState.NeedsReview, Revision = _entry.Revision + 1, TerminalReason = TriggerQueueTerminalReason.AmbiguousDispatch, TerminalAtUtc = renewedAtUtc, WorkerLease = _entry.WorkerLease! with { ReleasedAtUtc = renewedAtUtc }, Dispatch = dispatch };
                }

                return Result(_renewStatus);
            }

            Assert.Equal(_entry.Revision, expectedRevision);
            var lease = _entry.WorkerLease!;
            _entry = _entry with
            {
                Revision = _overflowRenewalValidation ? long.MinValue : _entry.Revision + 1,
                WorkerLease = lease with { ExpiresAtUtc = renewedAtUtc + leaseDuration, RenewalCount = lease.RenewalCount + 1 }
            };
            return Result(_renewStatus);
        }

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

            _entry = _entry with
            {
                State = TriggerQueueEntryState.Dispatching,
                Revision = _overflowRenewalValidation ? long.MaxValue : _entry.Revision + 1,
                Dispatch = intent
            };
            _onBegin?.Invoke();
            _begun.TrySetResult();
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
            CompletionAttempts.Add(outcome);
            _onComplete?.Invoke(CompleteCalls, outcome);
            CompletionExpectedRevision = expectedRevision;
            if (_completeException is not null && CompleteCalls <= _completeExceptionCount)
            {
                return Task.FromException<TriggerWorkerMutationResult>(_completeException);
            }

            if (CompleteCalls == 1 && _firstCompleteStatus is { } firstCompleteStatus)
            {
                return Result(firstCompleteStatus);
            }

            if (expectedRevision != _entry.Revision)
            {
                return Result(TriggerWorkerMutationStatus.RevisionConflict);
            }

            if (_entry.State != TriggerQueueEntryState.Dispatching)
            {
                return Result(TriggerWorkerMutationStatus.InvalidState);
            }

            var state = outcome.Outcome is TriggerDispatchOutcome.Accepted or TriggerDispatchOutcome.Terminal ? TriggerQueueEntryState.Dispatched : outcome.Outcome == TriggerDispatchOutcome.Rejected ? TriggerQueueEntryState.DispatchRejected : TriggerQueueEntryState.NeedsReview;
            var reason = state == TriggerQueueEntryState.Dispatched ? TriggerQueueTerminalReason.Dispatched : state == TriggerQueueEntryState.DispatchRejected ? TriggerQueueTerminalReason.DispatchRejected : TriggerQueueTerminalReason.AmbiguousDispatch;
            _entry = _entry with { State = state, Revision = _entry.Revision + 1, TerminalReason = reason, TerminalAtUtc = outcome.OutcomeRecordedAtUtc, Dispatch = outcome };
            return Result();
        }

        internal async Task WaitForBeginAsync()
        {
            await _begun.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        internal async Task WaitForRenewalsAsync(int count)
        {
            for (var attempt = 0; attempt < 500 && RenewCalls < count; attempt++)
            {
                await Task.Delay(10);
            }

            Assert.True(RenewCalls >= count, $"Expected at least {count} renewals but observed {RenewCalls}.");
        }

        private Task<TriggerWorkerMutationResult> Result(TriggerWorkerMutationStatus status = TriggerWorkerMutationStatus.Committed) => Task.FromResult(new TriggerWorkerMutationResult(status, 3, _entry));

        private static async Task<TriggerWorkerMutationResult> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("A renewal cancellation wait unexpectedly completed.");
        }
    }

    private sealed class ControlledTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ControlledTimer> _timers = [];
        private DateTimeOffset _now = now;

        internal Exception? UtcNowException { get; set; }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                if (UtcNowException is not null)
                {
                    throw UtcNowException;
                }

                return _now;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                var timer = new ControlledTimer(this, callback, state, _now + dueTime, period);
                _timers.Add(timer);
                return timer;
            }
        }

        internal async Task WaitForTimerCreationsAsync(int count)
        {
            for (var attempt = 0; attempt < 500; attempt++)
            {
                lock (_gate)
                {
                    if (_timers.Count >= count)
                    {
                        return;
                    }
                }

                await Task.Delay(10);
            }

            Assert.Fail($"Expected at least {count} timer registrations.");
        }

        internal void Advance(TimeSpan duration)
        {
            ControlledTimer[] due;
            lock (_gate)
            {
                _now += duration;
                due = _timers.Where(timer => timer.IsDue(_now)).ToArray();
            }

            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        internal void AdvanceClockWithoutFiringTimers(TimeSpan duration)
        {
            lock (_gate)
            {
                _now += duration;
            }
        }

        internal bool Change(ControlledTimer timer, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                return timer.ChangeCore(_now + dueTime, period);
            }
        }
    }

    private sealed class ControlledTimer(ControlledTimeProvider owner, TimerCallback callback, object? state, DateTimeOffset dueAtUtc, TimeSpan period) : ITimer
    {
        private readonly object _gate = new();
        private DateTimeOffset _dueAtUtc = dueAtUtc;
        private TimeSpan _period = period;
        private bool _disposed;

        internal bool IsDue(DateTimeOffset now)
        {
            lock (_gate)
            {
                return !_disposed && now >= _dueAtUtc;
            }
        }

        internal void Fire()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = _period == Timeout.InfiniteTimeSpan;
                if (!_disposed)
                {
                    _dueAtUtc += _period;
                }
            }

            callback(state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => owner.Change(this, dueTime, period);

        internal bool ChangeCore(DateTimeOffset dueAtUtc, TimeSpan period)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return false;
                }

                _dueAtUtc = dueAtUtc;
                _period = period;
                return true;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
