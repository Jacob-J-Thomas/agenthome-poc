using EmbodySense.Core.Application.Loops.Retry.Models;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Loops.Wait.Models;
using RunMutationStatus = EmbodySense.Core.Application.Loops.Retry.Models.RunMutationStatus;
using RunReadStatus = EmbodySense.Core.Application.Loops.Retry.Models.RunReadStatus;
using RetryRunMutationStatus = EmbodySense.Core.Application.Loops.Retry.Models.RunMutationStatus;
using RetryRunReadStatus = EmbodySense.Core.Application.Loops.Retry.Models.RunReadStatus;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Retry;
using EmbodySense.Core.Common.Loops.Execution.Retry.Models;
using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Common.Loops.Failures;
using EmbodySense.Core.Common.Loops.Failures.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Sequential;

namespace EmbodySense.Core.Application.Loops.Retry;

/// <summary>Durably retains, schedules, and publishes one exact opt-in retry attempt without dispatching it early.</summary>
public sealed class GovernedLoopRetryExecutionService : IGovernedLoopRetryNodeExecutor, IGovernedLoopWakeContinuationPort
{
    private readonly ICustomLoopRunStore _runStore;
    private readonly GovernedLoopSleepService _sleep;
    private readonly IGovernedLoopRetryCurrentPosturePort _currentPosture;
    private readonly IGovernedLoopRetryOrderedResumePort _orderedResume;

    /// <summary>Creates the retry orchestrator over canonical run, sleep, and current-posture boundaries.</summary>
    public GovernedLoopRetryExecutionService(
        ICustomLoopRunStore runStore,
        GovernedLoopSleepService sleep,
        IGovernedLoopRetryCurrentPosturePort currentPosture,
        IGovernedLoopRetryOrderedResumePort orderedResume)
    {
        _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
        _sleep = sleep ?? throw new ArgumentNullException(nameof(sleep));
        _currentPosture = currentPosture ?? throw new ArgumentNullException(nameof(currentPosture));
        _orderedResume = orderedResume ?? throw new ArgumentNullException(nameof(orderedResume));
    }

    /// <summary>Retains the first failure, parks the next attempt, and publishes its durable timestamp checkpoint.</summary>
    public async Task<GovernedLoopRetryExecutionResult> ScheduleAsync(
        GovernedLoopRetryExecutionRequest? request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryValidateRequest(request))
        {
            return Result(GovernedLoopRetryExecutionStatus.Conflict, detail: "retry-request-invalid");
        }

        var read = await ReadRunAsync(request!.Anchor.AdapterBinding.ExecutionBinding.RunId, cancellationToken).ConfigureAwait(false);
        if (read.Status != RunReadStatus.Found || read.Run is not { } run)
        {
            return Result(MapRead(read.Status), read.Run, detail: "retry-run-unavailable");
        }

        var existing = LatestRetry(run, request.Failure.ActivationOrdinal, request.Failure.VisitOrdinal);
        if (existing is { Disposition: GovernedLoopRetryStateDisposition.Scheduled, WakeCheckpointId: not null }
            && existing.CurrentAttempt == request.Failure.Attempt
            && string.Equals(existing.FailureEvidenceId, request.Failure.EvidenceId, StringComparison.Ordinal)
            && string.Equals(existing.FailureEvidenceHash, request.Failure.ContentHash, StringComparison.Ordinal))
        {
            return Result(GovernedLoopRetryExecutionStatus.Replayed, run, existing, "retry-schedule-replayed");
        }
        if (existing is { Disposition: GovernedLoopRetryStateDisposition.Exhausted or GovernedLoopRetryStateDisposition.Stopped or GovernedLoopRetryStateDisposition.NeedsReview }
            && existing.CurrentAttempt == request.Failure.Attempt
            && string.Equals(existing.FailureEvidenceId, request.Failure.EvidenceId, StringComparison.Ordinal)
            && string.Equals(existing.FailureEvidenceHash, request.Failure.ContentHash, StringComparison.Ordinal))
        {
            var replayStatus = existing.Disposition switch
            {
                GovernedLoopRetryStateDisposition.Exhausted => GovernedLoopRetryExecutionStatus.Exhausted,
                GovernedLoopRetryStateDisposition.Stopped => GovernedLoopRetryExecutionStatus.Ineligible,
                _ => GovernedLoopRetryExecutionStatus.NeedsReview,
            };
            return Result(replayStatus, run, existing, "retry-terminal-decision-replayed");
        }
        if (existing is not null
            && (existing.Disposition != GovernedLoopRetryStateDisposition.Dispatched
                || existing.NextAttempt != request.Failure.Attempt))
        {
            return Result(GovernedLoopRetryExecutionStatus.Conflict, run, existing, "retry-series-already-started");
        }

        var failureEvents = run.Events.Where(item => item.FailureEvidence is { } failure
            && string.Equals(failure.EvidenceId, request.Failure.EvidenceId, StringComparison.Ordinal)
            && string.Equals(failure.ContentHash, request.Failure.ContentHash, StringComparison.Ordinal)).Take(2).ToArray();
        var activation = run.Frontier?.Payload.Nodes.ElementAtOrDefault(request.Failure.ActivationOrdinal);
        if (failureEvents.Length != 1
            || activation is not { Status: GovernedLoopNodeExecutionStatus.Running, Attempt: { } currentAttempt, AttemptOperationId: not null }
            || currentAttempt != request.Failure.Attempt
            || activation.VisitOrdinal != request.Failure.VisitOrdinal
            || activation.PlanOrdinal != request.Node.Ordinal
            || !string.Equals(activation.NodeId, request.Node.NodeId, StringComparison.Ordinal)
            || existing is not null && !string.Equals(existing.AttemptOperationId, activation.AttemptOperationId, StringComparison.Ordinal)
            || request.Node.RetryPolicy is not { } policy)
        {
            return Result(GovernedLoopRetryExecutionStatus.Conflict, run, detail: "retry-failure-or-frontier-conflict");
        }

        GovernedLoopRetryCurrentPostureReadResult? postureRead;
        try
        {
            postureRead = await _currentPosture.ReadAsync(run, policy, request.Failure, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result(GovernedLoopRetryExecutionStatus.Unavailable, run, detail: "retry-posture-unavailable");
        }
        if (postureRead is not { Status: GovernedLoopRetryCurrentPostureReadStatus.Found, Posture: { } posture }
            || posture.ObservedAtUtc.Offset != TimeSpan.Zero
            || posture.ObservedAtUtc < run.UpdatedAtUtc)
        {
            return Result(postureRead?.Status == GovernedLoopRetryCurrentPostureReadStatus.Conflict ? GovernedLoopRetryExecutionStatus.Conflict : GovernedLoopRetryExecutionStatus.Unavailable, run, detail: "retry-posture-invalid");
        }

        var seriesStartedAtUtc = existing?.Identity.StartedAtUtc ?? FindSeriesStart(run, request.Failure);
        var decision = GovernedLoopRetryDecisionService.Evaluate(new GovernedLoopRetryEvaluationRequest(
            policy,
            request.Failure,
            existing?.Identity,
            currentAttempt,
            posture.Budget,
            seriesStartedAtUtc,
            posture.ObservedAtUtc,
            null,
            posture.LifecycleEligible,
            posture.AuthorityEligible,
            posture.DependenciesEligible));
        if (decision.Status is not (GovernedLoopRetryDecisionStatus.Schedule or GovernedLoopRetryDecisionStatus.Due)
            || decision.Series is not { } series
            || decision.NextAttempt is not { } nextAttempt
            || decision.EligibleAtUtc is not { } eligibleAtUtc
            || decision.AttemptOperationId is not { } operationId)
        {
            return decision.Series is not null
                && decision.Status is GovernedLoopRetryDecisionStatus.Exhausted or GovernedLoopRetryDecisionStatus.NoRetry or GovernedLoopRetryDecisionStatus.Ineligible or GovernedLoopRetryDecisionStatus.Cancelled or GovernedLoopRetryDecisionStatus.Paused or GovernedLoopRetryDecisionStatus.NeedsReview
                    ? await PersistTerminalDecisionAsync(run, existing, decision, activation.AttemptOperationId, currentAttempt, posture.Budget, request.Failure, posture.ObservedAtUtc, cancellationToken).ConfigureAwait(false)
                    : Result(MapDecision(decision.Status), run, detail: decision.Detail);
        }

        var retained = existing is null
            ? GovernedLoopRetryContract.CreateState(
                series,
                1,
                GovernedLoopRetryStateDisposition.FailureRetained,
                currentAttempt,
                activation.AttemptOperationId,
                null,
                null,
                posture.Budget,
                null,
                null,
                null,
                request.Failure.EvidenceId,
                request.Failure.ContentHash,
                posture.ObservedAtUtc)
            : GovernedLoopRetryContract.CreateState(
                series,
                existing.StateVersion + 1,
                GovernedLoopRetryStateDisposition.AttemptCompleted,
                currentAttempt,
                activation.AttemptOperationId,
                null,
                null,
                posture.Budget,
                null,
                null,
                null,
                request.Failure.EvidenceId,
                request.Failure.ContentHash,
                posture.ObservedAtUtc);
        var scheduled = GovernedLoopRetryContract.CreateState(
            series,
            retained.StateVersion + 1,
            GovernedLoopRetryStateDisposition.Scheduled,
            currentAttempt,
            activation.AttemptOperationId,
            nextAttempt,
            operationId,
            posture.Budget,
            eligibleAtUtc,
            null,
            null,
            request.Failure.EvidenceId,
            request.Failure.ContentHash,
            posture.ObservedAtUtc);
        var parked = GovernedLoopSequentialFrontierMachine.ParkRunningForRetry(
            run.Frontier,
            request.Anchor.AdapterBinding,
            request.Plan,
            request.Node,
            activation,
            currentAttempt,
            nextAttempt,
            operationId,
            posture.ObservedAtUtc);
        if (parked.Status != GovernedLoopSequentialFrontierTransitionStatus.Applied || parked.Frontier is null)
        {
            return Result(GovernedLoopRetryExecutionStatus.Conflict, run, detail: "retry-frontier-park-rejected");
        }

        var aggregateWaiting = parked.Frontier.Payload.Status == GovernedLoopFrontierStatus.Waiting;
        var events = new List<CustomLoopRunEvent>
        {
            StateEvent(run, retained, existing is null
                ? "Retry-safe failure evidence was retained before the next-attempt decision."
                : "The exact retry attempt completed with a newly retained retry-safe failure."),
        };
        events.Add(StateEvent(run with { Events = [.. run.Events, .. events] }, scheduled, "The exact bounded retry attempt entered durable sleep posture."));
        if (aggregateWaiting)
        {
            events.Add(LifecycleEvent(run with { Events = [.. run.Events, .. events] }, posture.ObservedAtUtc, "Ordered execution entered Waiting for an exact bounded retry."));
        }
        var candidate = Append(run, posture.ObservedAtUtc, events) with
        {
            Status = aggregateWaiting ? CustomLoopRunStatus.Waiting : CustomLoopRunStatus.Running,
            ExecutionClock = aggregateWaiting ? StopClock(run.ExecutionClock, posture.ObservedAtUtc) : run.ExecutionClock,
            Frontier = parked.Frontier,
        };
        var mutation = await UpdateAsync(run, candidate, current => HasState(current, scheduled), cancellationToken).ConfigureAwait(false);
        if (mutation.Status is not (RunMutationStatus.Committed or RunMutationStatus.Replayed) || mutation.Run is null)
        {
            return Result(MapMutation(mutation.Status), mutation.Run ?? run, detail: "retry-park-cas-incomplete");
        }

        return await PublishCheckpointAsync(mutation.Run, scheduled, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GovernedLoopRetryExecutionResult> PersistTerminalDecisionAsync(
        CustomLoopRunRecord run,
        GovernedLoopRetryState? existing,
        GovernedLoopRetryDecision decision,
        string currentAttemptOperationId,
        int currentAttempt,
        GovernedLoopRetryBudgetSnapshot budget,
        GovernedLoopFailureEvidence failure,
        DateTimeOffset recordedAtUtc,
        CancellationToken cancellationToken)
    {
        var retained = existing is null
            ? GovernedLoopRetryContract.CreateState(
                decision.Series!,
                1,
                GovernedLoopRetryStateDisposition.FailureRetained,
                currentAttempt,
                currentAttemptOperationId,
                null,
                null,
                budget,
                null,
                null,
                null,
                failure.EvidenceId,
                failure.ContentHash,
                recordedAtUtc)
            : GovernedLoopRetryContract.CreateState(
                decision.Series!,
                existing.StateVersion + 1,
                GovernedLoopRetryStateDisposition.AttemptCompleted,
                currentAttempt,
                currentAttemptOperationId,
                null,
                null,
                budget,
                null,
                null,
                null,
                failure.EvidenceId,
                failure.ContentHash,
                recordedAtUtc);
        var terminalDisposition = decision.Status switch
        {
            GovernedLoopRetryDecisionStatus.Exhausted => GovernedLoopRetryStateDisposition.Exhausted,
            GovernedLoopRetryDecisionStatus.NoRetry or GovernedLoopRetryDecisionStatus.Ineligible or GovernedLoopRetryDecisionStatus.Cancelled or GovernedLoopRetryDecisionStatus.Paused => GovernedLoopRetryStateDisposition.Stopped,
            _ => GovernedLoopRetryStateDisposition.NeedsReview,
        };
        var terminal = GovernedLoopRetryContract.CreateState(
            decision.Series!,
            retained.StateVersion + 1,
            terminalDisposition,
            currentAttempt,
            currentAttemptOperationId,
            null,
            null,
            budget,
            null,
            null,
            null,
            failure.EvidenceId,
            failure.ContentHash,
            recordedAtUtc);
        var events = new List<CustomLoopRunEvent>
        {
            StateEvent(run, retained, existing is null
                ? "Retry-safe failure evidence was retained before the terminal retry decision."
                : "The exact retry attempt completed before the terminal retry decision."),
        };
        events.Add(StateEvent(run with { Events = [.. run.Events, .. events] }, terminal, decision.Detail));
        var candidate = Append(run, recordedAtUtc, events);
        var mutation = await UpdateAsync(run, candidate, current => HasState(current, terminal), cancellationToken).ConfigureAwait(false);
        return mutation.Status is RunMutationStatus.Committed or RunMutationStatus.Replayed && mutation.Run is not null
            ? Result(MapDecision(decision.Status), mutation.Run, terminal, decision.Detail)
            : Result(MapMutation(mutation.Status), mutation.Run ?? run, retained, "retry-terminal-decision-incomplete");
    }

    /// <summary>Recovers a bounded set of retained schedules whose sleep checkpoint was not attached before restart.</summary>
    public async Task<GovernedLoopRetryRecoveryResult> RecoverAsync(int maximumCount, CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(maximumCount));
        IReadOnlyList<CustomLoopRunRecord> runs;
        try
        {
            runs = await _runStore.ListNonterminalAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new GovernedLoopRetryRecoveryResult(0, 1);
        }
        if (runs is null
            || runs.Any(candidate => candidate is null)
            || runs.Select(candidate => candidate.Id).Distinct(StringComparer.Ordinal).Count() != runs.Count)
        {
            return new GovernedLoopRetryRecoveryResult(0, 1);
        }

        var recovered = 0;
        var needsReview = runs.Count(candidate => HasPotentialRecoveryWork(candidate) && !CustomLoopRunValidator.Validate(candidate).IsValid);
        var candidates = runs.Where(candidate => CustomLoopRunValidator.Validate(candidate).IsValid)
            .SelectMany(run => LatestRetries(run)
                .Where(state => state is { Disposition: GovernedLoopRetryStateDisposition.Scheduled, WakeCheckpointId: null })
                .Select(state => new RetryRecoveryCandidate(run.Id, state.Identity.SeriesId, state.RecordedAtUtc)))
            .OrderBy(candidate => candidate.RecordedAtUtc)
            .ThenBy(candidate => candidate.RunId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.SeriesId, StringComparer.Ordinal)
            .Take(maximumCount)
            .ToArray();
        foreach (var candidate in candidates)
        {
            var current = await ReadRunAsync(candidate.RunId, cancellationToken).ConfigureAwait(false);
            var state = current.Run is null ? null : LatestRetry(current.Run, seriesId: candidate.SeriesId);
            if (current.Status != RunReadStatus.Found
                || current.Run is null
                || state is not { Disposition: GovernedLoopRetryStateDisposition.Scheduled })
            {
                needsReview++;
                continue;
            }
            if (state.WakeCheckpointId is not null)
            {
                recovered++;
                continue;
            }

            var published = await PublishCheckpointCoreAsync(current.Run, state, cancellationToken).ConfigureAwait(false);
            if (published.Status is GovernedLoopRetryExecutionStatus.Scheduled or GovernedLoopRetryExecutionStatus.Replayed)
            {
                recovered++;
            }
            else
            {
                needsReview++;
            }
        }
        return new GovernedLoopRetryRecoveryResult(recovered, needsReview);
    }

    /// <inheritdoc />
    public Task<GovernedLoopWakeContinuationResult?> ContinueAsync(
        GovernedLoopWakeContinuationRequest request,
        CancellationToken cancellationToken = default)
        => ContinueCoreAsync(request, reconcileOnly: false, cancellationToken);

    /// <inheritdoc />
    public Task<GovernedLoopWakeContinuationResult?> ReconcileAsync(
        GovernedLoopWakeContinuationRequest request,
        CancellationToken cancellationToken = default)
        => ContinueCoreAsync(request, reconcileOnly: true, cancellationToken);

    private async Task<GovernedLoopWakeContinuationResult?> ContinueCoreAsync(
        GovernedLoopWakeContinuationRequest request,
        bool reconcileOnly,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryValidateContinuationRequest(request, reconcileOnly))
        {
            return Continuation(GovernedLoopWakeContinuationStatus.Conflict, reference: "retry-continuation-invalid");
        }

        var read = await ReadRunAsync(request.Checkpoint.Binding.Execution.RunId, cancellationToken).ConfigureAwait(false);
        if (read.Status != RunReadStatus.Found || read.Run is not { } run)
        {
            return Continuation(read.Status == RunReadStatus.Unavailable ? GovernedLoopWakeContinuationStatus.Unavailable : GovernedLoopWakeContinuationStatus.Conflict, reference: "retry-run-unavailable");
        }

        var selection = FindRetryForCheckpoint(run, request.Checkpoint);
        if (selection is null)
        {
            return Continuation(reconcileOnly ? GovernedLoopWakeContinuationStatus.NotCommitted : GovernedLoopWakeContinuationStatus.Conflict, reference: "retry-state-not-found");
        }
        if (selection.DispatchWasCommitted && !MatchesCheckpoint(selection.Latest, request.Checkpoint))
        {
            return Continuation(GovernedLoopWakeContinuationStatus.Committed, selection.Latest.ContentHash);
        }
        var state = selection.Latest;
        if (!MatchesCheckpoint(state, request.Checkpoint))
        {
            return Continuation(GovernedLoopWakeContinuationStatus.Conflict, reference: "retry-checkpoint-substituted");
        }

        var context = await ResolveContextAsync(run, cancellationToken).ConfigureAwait(false);
        var activation = run.Frontier?.Payload.Nodes.ElementAtOrDefault(state.Identity.ActivationOrdinal);
        var node = context?.Plan.Nodes.ElementAtOrDefault(activation?.PlanOrdinal ?? -1);
        if (context is null
            || activation is null
            || node?.RetryPolicy is not { } policy
            || !string.Equals(policy.ContentHash, state.Identity.PolicyHash, StringComparison.Ordinal)
            || !string.Equals(policy.PolicyId, state.Identity.PolicyId, StringComparison.Ordinal))
        {
            return Continuation(GovernedLoopWakeContinuationStatus.Conflict, reference: "retry-context-substituted");
        }

        if (state.Disposition == GovernedLoopRetryStateDisposition.Dispatched)
        {
            return await ResumeOrderedAsync(run, context, state, cancellationToken).ConfigureAwait(false);
        }
        if (reconcileOnly)
        {
            return Continuation(GovernedLoopWakeContinuationStatus.NotCommitted, reference: "retry-dispatch-not-retained");
        }
        if (state.Disposition != GovernedLoopRetryStateDisposition.Scheduled
            || activation.Status != GovernedLoopNodeExecutionStatus.Waiting
            || activation.Attempt != state.NextAttempt
            || !string.Equals(activation.AttemptOperationId, state.AttemptOperationId, StringComparison.Ordinal))
        {
            return Continuation(GovernedLoopWakeContinuationStatus.Conflict, reference: "retry-waiting-state-conflict");
        }

        var retainedFailure = FindFailure(run, state);
        if (retainedFailure is null)
        {
            return Continuation(GovernedLoopWakeContinuationStatus.Conflict, reference: "retry-failure-evidence-unavailable");
        }
        var posture = await ReadCurrentPostureAsync(run, policy, retainedFailure, cancellationToken).ConfigureAwait(false);
        if (posture is null)
        {
            return Continuation(GovernedLoopWakeContinuationStatus.Unavailable, reference: "retry-current-posture-unavailable");
        }
        if (!posture.LifecycleEligible || !posture.AuthorityEligible || !posture.DependenciesEligible)
        {
            return await TerminalizeWaitingAsync(
                run,
                context,
                state,
                activation,
                GovernedLoopRetryStateDisposition.Stopped,
                posture.Budget,
                posture.ObservedAtUtc,
                "retry-current-posture-ineligible",
                cancellationToken).ConfigureAwait(false);
        }
        if (posture.ObservedAtUtc < request.PreparedWakeEvidence!.RecordedAtUtc
            || posture.Budget.Attempts != state.CurrentAttempt
            || !BudgetDominates(state.Budget, posture.Budget))
        {
            return await TerminalizeWaitingAsync(
                run,
                context,
                state,
                activation,
                GovernedLoopRetryStateDisposition.NeedsReview,
                state.Budget,
                Max(state.RecordedAtUtc, posture.ObservedAtUtc),
                "retry-budget-evidence-conflict",
                cancellationToken).ConfigureAwait(false);
        }
        var budgetDisposition = BudgetDisposition(policy, posture.Budget);
        if (budgetDisposition is { } budgetTerminal)
        {
            return await TerminalizeWaitingAsync(
                run,
                context,
                state,
                activation,
                budgetTerminal,
                posture.Budget,
                posture.ObservedAtUtc,
                budgetTerminal == GovernedLoopRetryStateDisposition.Exhausted ? "retry-budget-exhausted" : "retry-budget-evidence-unavailable",
                cancellationToken).ConfigureAwait(false);
        }
        if (!AttemptFitsDeadline(policy, state, posture.ObservedAtUtc))
        {
            return await TerminalizeWaitingAsync(
                run,
                context,
                state,
                activation,
                GovernedLoopRetryStateDisposition.Exhausted,
                posture.Budget,
                posture.ObservedAtUtc,
                "retry-deadline-exhausted",
                cancellationToken).ConfigureAwait(false);
        }
        if (!TryReserveBudget(policy, posture.Budget, state.NextAttempt!.Value, out var reservedBudget))
        {
            return await TerminalizeWaitingAsync(
                run,
                context,
                state,
                activation,
                GovernedLoopRetryStateDisposition.NeedsReview,
                posture.Budget,
                posture.ObservedAtUtc,
                "retry-budget-reservation-unproven",
                cancellationToken).ConfigureAwait(false);
        }

        var due = Successor(state, GovernedLoopRetryStateDisposition.Due, state.Budget, posture.ObservedAtUtc);
        var reserved = Successor(due, GovernedLoopRetryStateDisposition.Reserved, reservedBudget!, posture.ObservedAtUtc);
        var dispatched = Successor(reserved, GovernedLoopRetryStateDisposition.Dispatched, reservedBudget!, posture.ObservedAtUtc);
        var resumed = GovernedLoopSequentialFrontierMachine.ResumeRetry(
            run.Frontier,
            run.SequentialAdapterBinding,
            context.Plan,
            activation,
            state.NextAttempt.Value,
            state.AttemptOperationId,
            posture.ObservedAtUtc);
        if (resumed.Status != GovernedLoopSequentialFrontierTransitionStatus.Applied || resumed.Frontier is null)
        {
            return Continuation(GovernedLoopWakeContinuationStatus.Conflict, reference: "retry-frontier-resume-rejected");
        }

        var stateEvents = new List<CustomLoopRunEvent>
        {
            StateEvent(run, due, "The exact durable retry wake became due."),
        };
        stateEvents.Add(StateEvent(run with { Events = [.. run.Events, .. stateEvents] }, reserved, "The bounded next-attempt budget was reserved before dispatch."));
        stateEvents.Add(StateEvent(run with { Events = [.. run.Events, .. stateEvents] }, dispatched, "The exact retry reservation reached its canonical ordered-dispatch boundary."));
        if (run.Status == CustomLoopRunStatus.Waiting)
        {
            stateEvents.Add(LifecycleEvent(run with { Events = [.. run.Events, .. stateEvents] }, posture.ObservedAtUtc, "Ordered execution resumed for one exact bounded retry."));
        }
        var candidate = Append(run, posture.ObservedAtUtc, stateEvents) with
        {
            Status = CustomLoopRunStatus.Running,
            ExecutionClock = run.Status == CustomLoopRunStatus.Waiting ? run.ExecutionClock with { ActiveSinceUtc = posture.ObservedAtUtc } : run.ExecutionClock,
            Frontier = resumed.Frontier,
        };
        var mutation = await UpdateAsync(run, candidate, current => HasState(current, dispatched), cancellationToken).ConfigureAwait(false);
        if (mutation.Status is not (RunMutationStatus.Committed or RunMutationStatus.Replayed) || mutation.Run is null)
        {
            return Continuation(mutation.Status == RunMutationStatus.Conflict ? GovernedLoopWakeContinuationStatus.Conflict : GovernedLoopWakeContinuationStatus.Ambiguous, reference: "retry-dispatch-cas-incomplete");
        }

        return await ResumeOrderedAsync(mutation.Run, context, dispatched, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<GovernedLoopWakeContinuationResult> TerminalizeWaitingAsync(
        CustomLoopRunRecord run,
        GovernedLoopWaitOrderedContext context,
        GovernedLoopRetryState scheduled,
        GovernedLoopNodeExecutionEvidence activation,
        GovernedLoopRetryStateDisposition disposition,
        GovernedLoopRetryBudgetSnapshot budget,
        DateTimeOffset recordedAtUtc,
        string detail,
        CancellationToken cancellationToken)
    {
        var originatingFailureEvent = FindFailureEvent(run, scheduled);
        if (originatingFailureEvent?.SequentialNodeEvidence is not { } failureEvidence
            || failureEvidence.Kind != CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection
            || failureEvidence.Disposition != CustomLoopSequentialNodeDisposition.Rejected
            || !CustomLoopSequentialNodeEvidenceHash.Matches(failureEvidence)
            || !CustomLoopSequentialOutcomeArtifactHash.Matches(originatingFailureEvent))
        {
            return Continuation(GovernedLoopWakeContinuationStatus.Ambiguous, reference: "retry-terminal-failure-evidence-unavailable");
        }

        var states = new List<GovernedLoopRetryState>();
        var current = scheduled;
        if (disposition == GovernedLoopRetryStateDisposition.Exhausted)
        {
            current = Successor(scheduled, GovernedLoopRetryStateDisposition.Due, budget, recordedAtUtc);
            states.Add(current);
        }
        var terminal = TerminalSuccessor(current, disposition, budget, recordedAtUtc);
        states.Add(terminal);
        var events = new List<CustomLoopRunEvent>();
        CustomLoopRunEvent? terminalEvent = null;
        foreach (var state in states)
        {
            var stateEvent = StateEvent(run with { Events = [.. run.Events, .. events] }, state, state == terminal ? detail : "The exact durable retry wake became due before the hard bound was exhausted.");
            events.Add(stateEvent);
            if (state == terminal)
            {
                terminalEvent = stateEvent;
            }
        }
        if (terminalEvent is null)
        {
            return Continuation(GovernedLoopWakeContinuationStatus.Ambiguous, reference: "retry-terminal-state-evidence-unavailable");
        }
        if (disposition == GovernedLoopRetryStateDisposition.Exhausted)
        {
            var exhaustedEvent = CreateExhaustionEvent(run with { Events = [.. run.Events, .. events] }, context.Plan, activation, terminalEvent, terminal, recordedAtUtc, detail);
            events.Add(exhaustedEvent);
            var node = context.Plan.Nodes.ElementAtOrDefault(activation.PlanOrdinal);
            if (node is null)
            {
                return Continuation(GovernedLoopWakeContinuationStatus.Ambiguous, reference: "retry-exhaustion-node-unavailable");
            }
            var skipReferences = new List<GovernedLoopSequentialSkipEvidenceReference>();
            if (context.Plan.ControlEdges.Any(edge => string.Equals(edge.FromNodeId, activation.NodeId, StringComparison.Ordinal)
                && edge.Condition == GovernedLoopControlCondition.Failure))
            {
                var pruning = GovernedLoopSequentialFrontierMachine.PlanPruning(
                    run.Frontier,
                    run.SequentialAdapterBinding,
                    context.Plan,
                    activation,
                    GovernedLoopControlCondition.Failure);
                if (pruning.Status != GovernedLoopSequentialFrontierTransitionStatus.Applied)
                {
                    return Continuation(GovernedLoopWakeContinuationStatus.Ambiguous, reference: "retry-exhaustion-pruning-unproven");
                }
                foreach (var pruned in pruning.Activations)
                {
                    var skipped = CreateSkipEvent(run with { Events = [.. run.Events, .. events] }, pruned, recordedAtUtc);
                    events.Add(skipped);
                    skipReferences.Add(new GovernedLoopSequentialSkipEvidenceReference(
                        pruned.Activation.ActivationOrdinal,
                        pruned.GoverningActivationOrdinal,
                        pruned.GoverningControlEdgeId,
                        skipped.EventId,
                        skipped.SequentialNodeEvidence!.OutcomeArtifactHash));
                }
            }
            var failed = GovernedLoopSequentialFrontierMachine.FailWaiting(
                run.Frontier,
                run.SequentialAdapterBinding,
                context.Plan,
                node,
                activation,
                activation.Attempt!.Value,
                activation.AttemptOperationId,
                exhaustedEvent.EventId,
                exhaustedEvent.SequentialNodeEvidence!.OutcomeArtifactHash,
                GovernedLoopControlCondition.Failure,
                recordedAtUtc,
                skipReferences,
                FindCycleStartedAtUtc(run, activation.CycleId));
            if (failed.Status != GovernedLoopSequentialFrontierTransitionStatus.Applied || failed.Frontier is null)
            {
                return Continuation(GovernedLoopWakeContinuationStatus.Ambiguous, reference: "retry-exhaustion-frontier-unproven");
            }

            var routed = failed.Frontier.Payload.Status == GovernedLoopFrontierStatus.Active;
            if (!routed && failed.Frontier.Payload.Status != GovernedLoopFrontierStatus.Failed)
            {
                return Continuation(GovernedLoopWakeContinuationStatus.Ambiguous, reference: "retry-exhaustion-lifecycle-unproven");
            }
            events.Add(LifecycleEvent(
                run with { Events = [.. run.Events, .. events] },
                recordedAtUtc,
                routed
                    ? "The retry budget was exhausted without dispatch and ordered execution continued through the admitted Failure route."
                    : "The retry budget was exhausted without dispatch and the run stopped with exact classified evidence."));
            var exhaustedRun = Append(run, recordedAtUtc, events) with
            {
                Status = routed ? CustomLoopRunStatus.Running : CustomLoopRunStatus.Failed,
                CompletedAtUtc = routed ? null : recordedAtUtc,
                ExecutionClock = routed ? run.ExecutionClock with { ActiveSinceUtc = recordedAtUtc } : StopClock(run.ExecutionClock, recordedAtUtc),
                FailureCode = routed ? null : $"canonical_{detail.Replace('-', '_')}",
                FailureDetail = routed ? null : detail,
                FinalOutput = null,
                Frontier = failed.Frontier,
            };
            var exhaustedMutation = await UpdateAsync(
                run,
                exhaustedRun,
                durable => durable.Status == exhaustedRun.Status
                    && string.Equals(durable.Frontier?.Payload.ContentHash, exhaustedRun.Frontier.Payload.ContentHash, StringComparison.Ordinal)
                    && HasState(durable, terminal),
                cancellationToken).ConfigureAwait(false);
            if (exhaustedMutation.Status is not (RunMutationStatus.Committed or RunMutationStatus.Replayed) || exhaustedMutation.Run is null)
            {
                return Continuation(GovernedLoopWakeContinuationStatus.Ambiguous, reference: "retry-exhaustion-cas-incomplete");
            }
            return routed
                ? await ResumeOrderedAsync(exhaustedMutation.Run, context, terminal, CancellationToken.None).ConfigureAwait(false)
                : Continuation(GovernedLoopWakeContinuationStatus.Committed, terminal.ContentHash);
        }

        var blocked = GovernedLoopSequentialFrontierMachine.ReviewBlockWaiting(
            run.Frontier,
            run.SequentialAdapterBinding,
            context.Plan,
            activation,
            terminalEvent.EventId,
            terminal.ContentHash,
            recordedAtUtc);
        if (blocked.Status != GovernedLoopSequentialFrontierTransitionStatus.Applied || blocked.Frontier is null)
        {
            return Continuation(GovernedLoopWakeContinuationStatus.Ambiguous, reference: "retry-terminal-frontier-unproven");
        }
        events.Add(LifecycleEvent(run with { Events = [.. run.Events, .. events] }, recordedAtUtc, "Automatic retry stopped without dispatch; exact retained evidence is available for review."));
        var candidate = Append(run, recordedAtUtc, events) with
        {
            Status = CustomLoopRunStatus.NeedsReview,
            CompletedAtUtc = recordedAtUtc,
            ExecutionClock = StopClock(run.ExecutionClock, recordedAtUtc),
            FailureCode = $"canonical_{detail.Replace('-', '_')}",
            FailureDetail = detail,
            FinalOutput = null,
            Frontier = blocked.Frontier,
        };
        var mutation = await UpdateAsync(
            run,
            candidate,
            durable => durable.Status == CustomLoopRunStatus.NeedsReview && HasState(durable, terminal),
            cancellationToken).ConfigureAwait(false);
        return mutation.Status is RunMutationStatus.Committed or RunMutationStatus.Replayed && mutation.Run is not null
            ? Continuation(GovernedLoopWakeContinuationStatus.Committed, terminal.ContentHash)
            : Continuation(GovernedLoopWakeContinuationStatus.Ambiguous, reference: "retry-terminal-cas-incomplete");
    }

    private static CustomLoopRunEvent CreateExhaustionEvent(
        CustomLoopRunRecord run,
        GovernedLoopSequentialPlan plan,
        GovernedLoopNodeExecutionEvidence activation,
        CustomLoopRunEvent terminalStateEvent,
        GovernedLoopRetryState terminal,
        DateTimeOffset recordedAtUtc,
        string detail)
    {
        var binding = run.SequentialAdapterBinding!;
        var selectedEdges = plan.ControlEdges
            .Where(edge => string.Equals(edge.FromNodeId, activation.NodeId, StringComparison.Ordinal)
                && edge.Condition == GovernedLoopControlCondition.Failure)
            .Select(edge => edge.Id)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var skippedEdges = activation.OutgoingControlEdgeIds.Except(selectedEdges, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var failure = GovernedLoopFailureEvidenceContract.Create(
            $"retry-exhaustion-{terminal.Identity.SeriesId[..16]}-{terminal.StateVersion}",
            binding.WorkspaceId,
            run.Id,
            binding.ExecutionBinding.Revision,
            binding.ExecutionBinding.ExecutionGeneration,
            activation.ActivationOrdinal,
            activation.VisitOrdinal,
            activation.NodeId,
            activation.Attempt!.Value,
            GovernedLoopFailureClass.Exhaustion,
            detail,
            GovernedLoopFailureSource.Runtime,
            GovernedLoopFailureEffectCertainty.DispatchProvedNotStarted,
            GovernedLoopFailureAuthorityPosture.Current,
            GovernedLoopFailureHumanPosture.None,
            GovernedLoopFailureRetrySafety.NotRetryable,
            GovernedLoopFailureSeverity.Error,
            900,
            [new GovernedLoopFailureEvidenceReference(terminalStateEvent.EventId, terminal.ContentHash)],
            "retry budget exhausted before dispatch",
            recordedAtUtc);
        var runEvent = new CustomLoopRunEvent(
            run.Events.Length + 1,
            failure.EvidenceId,
            recordedAtUtc,
            CustomLoopRunEventKind.NodeAttemptFailed,
            activation.CycleIteration ?? run.Checkpoint.Iteration,
            activation.NodeId,
            activation.Attempt,
            "The exact retry budget was exhausted before another attempt could dispatch.",
            [],
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null)
        {
            FailureEvidence = failure,
        };
        var evidence = CustomLoopSequentialNodeEvidenceHash.Apply(new CustomLoopSequentialNodeEvidence(
            CustomLoopSequentialNodeEvidence.CurrentSchemaVersion,
            CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection,
            binding.WorkspaceId,
            run.Id,
            binding.ExecutionBinding.Revision,
            binding.ExecutionBinding.ExecutionGeneration,
            activation.ActivationOrdinal,
            activation.VisitOrdinal,
            activation.NodeId,
            activation.Attempt,
            activation.CycleId,
            activation.CycleIteration,
            GovernedLoopControlCondition.Failure,
            selectedEdges,
            skippedEdges,
            null,
            null,
            CustomLoopSequentialNodeDisposition.Rejected,
            CustomLoopSequentialOutcomeArtifactHash.Compute(runEvent),
            string.Empty)
        {
            FailureEvidenceId = failure.EvidenceId,
            FailureEvidenceHash = failure.ContentHash,
        });
        return runEvent with { SequentialNodeEvidence = evidence };
    }

    private static CustomLoopRunEvent CreateSkipEvent(
        CustomLoopRunRecord run,
        GovernedLoopSequentialPrunedActivation pruning,
        DateTimeOffset recordedAtUtc)
    {
        var activation = pruning.Activation;
        var runEvent = new CustomLoopRunEvent(
            run.Events.Length + 1,
            $"retry-prune-{pruning.GoverningActivationOrdinal}-{activation.ActivationOrdinal}",
            recordedAtUtc,
            CustomLoopRunEventKind.TopologyNodeSkipped,
            activation.CycleIteration ?? run.Checkpoint.Iteration,
            activation.NodeId,
            null,
            $"Activation `{activation.ActivationOrdinal}` was pruned by exact retry-exhaustion edge selection `{pruning.GoverningControlEdgeId}`.",
            [],
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);
        var binding = run.SequentialAdapterBinding!;
        var evidence = CustomLoopSequentialNodeEvidenceHash.Apply(new CustomLoopSequentialNodeEvidence(
            CustomLoopSequentialNodeEvidence.CurrentSchemaVersion,
            CustomLoopSequentialNodeEvidenceKind.TopologySkipped,
            binding.WorkspaceId,
            binding.ExecutionBinding.RunId,
            binding.ExecutionBinding.Revision,
            binding.ExecutionBinding.ExecutionGeneration,
            activation.ActivationOrdinal,
            activation.VisitOrdinal,
            activation.NodeId,
            null,
            activation.CycleId,
            activation.CycleIteration,
            null,
            [],
            [],
            pruning.GoverningActivationOrdinal,
            pruning.GoverningControlEdgeId,
            CustomLoopSequentialNodeDisposition.Completed,
            CustomLoopSequentialOutcomeArtifactHash.Compute(runEvent),
            string.Empty));
        return runEvent with { SequentialNodeEvidence = evidence };
    }

    private async Task<GovernedLoopRetryExecutionResult> PublishCheckpointAsync(
        CustomLoopRunRecord run,
        GovernedLoopRetryState scheduled,
        CancellationToken cancellationToken)
        => await PublishCheckpointCoreAsync(run, scheduled, cancellationToken).ConfigureAwait(false);

    private async Task<GovernedLoopWaitOrderedContext?> ResolveContextAsync(CustomLoopRunRecord run, CancellationToken cancellationToken)
    {
        try
        {
            return await _orderedResume.ResolveAsync(run, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task<GovernedLoopRetryCurrentPosture?> ReadCurrentPostureAsync(
        CustomLoopRunRecord run,
        GovernedLoopRetryPolicy policy,
        GovernedLoopFailureEvidence failure,
        CancellationToken cancellationToken)
    {
        GovernedLoopRetryCurrentPostureReadResult? read;
        try
        {
            read = await _currentPosture.ReadAsync(run, policy, failure, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }

        return read is { Status: GovernedLoopRetryCurrentPostureReadStatus.Found, Posture: { } posture }
            && posture.ObservedAtUtc.Offset == TimeSpan.Zero
            && posture.ObservedAtUtc >= run.UpdatedAtUtc
                ? posture
                : null;
    }

    private async Task<GovernedLoopWakeContinuationResult> ResumeOrderedAsync(
        CustomLoopRunRecord run,
        GovernedLoopWaitOrderedContext context,
        GovernedLoopRetryState state,
        CancellationToken cancellationToken)
    {
        CustomLoopOrderedRunResult? resumed;
        try
        {
            resumed = await _orderedResume.ResumeRetryAsync(
                new GovernedLoopRetryOrderedResumeRequest(context, state, run.AdmissionActor),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            resumed = null;
        }

        var read = await ReadRunAsync(run.Id, CancellationToken.None).ConfigureAwait(false);
        var durableState = read.Run is null ? null : LatestRetry(read.Run, seriesId: state.Identity.SeriesId);
        if (durableState is null
            || !string.Equals(durableState.Identity.SeriesId, state.Identity.SeriesId, StringComparison.Ordinal)
            || durableState.StateVersion < state.StateVersion)
        {
            return Continuation(GovernedLoopWakeContinuationStatus.Ambiguous, reference: "retry-dispatch-evidence-unavailable");
        }

        return IsConclusiveOrderedResume(resumed, read.Run)
            ? Continuation(GovernedLoopWakeContinuationStatus.Committed, state.ContentHash)
            : Continuation(GovernedLoopWakeContinuationStatus.Ambiguous, reference: "ordered-retry-reentry-incomplete");
    }

    private static bool IsConclusiveOrderedResume(CustomLoopOrderedRunResult? resumed, CustomLoopRunRecord? durable)
        => resumed?.Status switch
        {
            CustomLoopOrderedRunStatus.Completed => durable?.Status == CustomLoopRunStatus.Completed,
            CustomLoopOrderedRunStatus.Waiting => durable?.Status == CustomLoopRunStatus.Waiting,
            CustomLoopOrderedRunStatus.Paused => durable?.Status == CustomLoopRunStatus.Paused,
            CustomLoopOrderedRunStatus.Failed => durable?.Status == CustomLoopRunStatus.Failed,
            CustomLoopOrderedRunStatus.NeedsReview => durable?.Status == CustomLoopRunStatus.NeedsReview,
            CustomLoopOrderedRunStatus.Cancelled => durable?.Status == CustomLoopRunStatus.Cancelled,
            _ => false,
        };

    private static GovernedLoopRetryState Successor(
        GovernedLoopRetryState current,
        GovernedLoopRetryStateDisposition disposition,
        GovernedLoopRetryBudgetSnapshot budget,
        DateTimeOffset recordedAtUtc)
        => GovernedLoopRetryContract.CreateState(
            current.Identity,
            checked(current.StateVersion + 1),
            disposition,
            current.CurrentAttempt,
            current.CurrentAttemptOperationId,
            current.NextAttempt,
            current.AttemptOperationId,
            budget,
            null,
            current.WakeCheckpointId,
            current.WakeCheckpointHash,
            current.FailureEvidenceId,
            current.FailureEvidenceHash,
            recordedAtUtc);

    private static GovernedLoopRetryState TerminalSuccessor(
        GovernedLoopRetryState current,
        GovernedLoopRetryStateDisposition disposition,
        GovernedLoopRetryBudgetSnapshot budget,
        DateTimeOffset recordedAtUtc)
        => GovernedLoopRetryContract.CreateState(
            current.Identity,
            checked(current.StateVersion + 1),
            disposition,
            current.CurrentAttempt,
            current.CurrentAttemptOperationId,
            null,
            null,
            budget,
            null,
            null,
            null,
            current.FailureEvidenceId,
            current.FailureEvidenceHash,
            recordedAtUtc);

    private static GovernedLoopRetryStateDisposition? BudgetDisposition(
        GovernedLoopRetryPolicy policy,
        GovernedLoopRetryBudgetSnapshot budget)
    {
        if (policy.MaximumTokens is not null && budget.Tokens is null
            || policy.MaximumToolCalls is not null && budget.ToolCalls is null
            || policy.MaximumCostMicrounits is not null && budget.CostMicrounits is null
            || policy.MaximumResourceUnits is not null && budget.ResourceUnits is null)
        {
            return GovernedLoopRetryStateDisposition.NeedsReview;
        }
        if (policy.MaximumTokens is { } maximumTokens && budget.Tokens >= maximumTokens
            || policy.MaximumToolCalls is { } maximumTools && budget.ToolCalls >= maximumTools
            || policy.MaximumCostMicrounits is { } maximumCost && budget.CostMicrounits >= maximumCost
            || policy.MaximumResourceUnits is { } maximumResources && budget.ResourceUnits >= maximumResources)
        {
            return GovernedLoopRetryStateDisposition.Exhausted;
        }
        return null;
    }

    private static bool AttemptFitsDeadline(
        GovernedLoopRetryPolicy policy,
        GovernedLoopRetryState state,
        DateTimeOffset observedAtUtc)
    {
        try
        {
            return observedAtUtc <= state.Identity.DeadlineUtc
                && observedAtUtc.AddMilliseconds(policy.PerAttemptTimeoutMilliseconds) <= state.Identity.DeadlineUtc;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool TryReserveBudget(
        GovernedLoopRetryPolicy policy,
        GovernedLoopRetryBudgetSnapshot budget,
        int nextAttempt,
        out GovernedLoopRetryBudgetSnapshot? reserved)
    {
        reserved = null;
        if (nextAttempt != budget.Attempts + 1)
        {
            return false;
        }

        int? resourceUnits = budget.ResourceUnits;
        if (policy.MaximumResourceUnits is not null)
        {
            if (resourceUnits is null || resourceUnits.Value >= policy.MaximumResourceUnits.Value)
            {
                return false;
            }
            resourceUnits = checked(resourceUnits.Value + 1);
        }

        reserved = budget with { Attempts = nextAttempt, ResourceUnits = resourceUnits };
        return true;
    }

    private static bool BudgetDominates(GovernedLoopRetryBudgetSnapshot retained, GovernedLoopRetryBudgetSnapshot current)
        => current.Attempts >= retained.Attempts
            && Dominates(retained.Tokens, current.Tokens)
            && Dominates(retained.ToolCalls, current.ToolCalls)
            && Dominates(retained.CostMicrounits, current.CostMicrounits)
            && string.Equals(retained.CostCurrency, current.CostCurrency, StringComparison.Ordinal)
            && Dominates(retained.ResourceUnits, current.ResourceUnits);

    private static bool Dominates(long? retained, long? current)
        => retained is null || current is not null && current.Value >= retained.Value;

    private static bool Dominates(int? retained, int? current)
        => retained is null || current is not null && current.Value >= retained.Value;

    private static bool TryValidateContinuationRequest(GovernedLoopWakeContinuationRequest? request, bool reconcileOnly)
    {
        if (request?.Checkpoint is null
            || request.Identity is null
            || !GovernedLoopSleepContractValidator.Validate(request.Checkpoint).IsValid
            || !string.Equals(request.Checkpoint.CheckpointId, request.Identity.CheckpointId, StringComparison.Ordinal)
            || !string.Equals(request.Checkpoint.ContentHash, request.Identity.CheckpointHash, StringComparison.Ordinal)
            || !CustomLoopArtifactIdentifier.IsValid(request.ContinuationOperationId)
            || !reconcileOnly && (request.PreparedWakeEvidence is null || !IsHash(request.ExpectedPostureHash)))
        {
            return false;
        }

        var prepared = request.PreparedWakeEvidence;
        return prepared is null
            || prepared.Disposition == GovernedLoopWakeDisposition.Prepared
                && GovernedLoopSleepContractValidator.Validate(prepared).IsValid
                && GovernedLoopSleepContractValidator.ValidateComposition(request.Checkpoint, prepared).IsValid
                && string.Equals(prepared.Identity.ContentHash, request.Identity.ContentHash, StringComparison.Ordinal)
                && string.Equals(prepared.ContinuationOperationId, request.ContinuationOperationId, StringComparison.Ordinal);
    }

    private static bool MatchesCheckpoint(GovernedLoopRetryState state, GovernedLoopSleepCheckpoint checkpoint)
        => state.Disposition is GovernedLoopRetryStateDisposition.Scheduled or GovernedLoopRetryStateDisposition.Due or GovernedLoopRetryStateDisposition.Reserved or GovernedLoopRetryStateDisposition.Dispatched
            && string.Equals(state.WakeCheckpointId, checkpoint.CheckpointId, StringComparison.Ordinal)
            && string.Equals(state.WakeCheckpointHash, checkpoint.ContentHash, StringComparison.Ordinal)
            && state.Identity.ActivationOrdinal == checkpoint.Binding.ActivationOrdinal
            && state.Identity.VisitOrdinal == checkpoint.Binding.NodeVisitOrdinal
            && string.Equals(state.Identity.NodeId, checkpoint.Binding.NodeId, StringComparison.Ordinal)
            && state.NextAttempt == checkpoint.Binding.WaitAttempt
            && string.Equals(state.AttemptOperationId, checkpoint.Binding.WaitOperationId, StringComparison.Ordinal);

    private static GovernedLoopWakeContinuationResult Continuation(
        GovernedLoopWakeContinuationStatus status,
        string? evidenceHash = null,
        string? reference = null)
        => new(status, evidenceHash, reference);

    private static bool IsHash(string? value)
        => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private async Task<GovernedLoopRetryExecutionResult> PublishCheckpointCoreAsync(
        CustomLoopRunRecord run,
        GovernedLoopRetryState scheduled,
        CancellationToken cancellationToken)
    {
        if (scheduled.WakeCheckpointId is not null)
        {
            return Result(GovernedLoopRetryExecutionStatus.Replayed, run, scheduled, "retry-checkpoint-replayed");
        }
        var activation = run.Frontier?.Payload.Nodes.ElementAtOrDefault(scheduled.Identity.ActivationOrdinal);
        var binding = run.SequentialAdapterBinding;
        if (activation is not { Status: GovernedLoopNodeExecutionStatus.Waiting }
            || binding is null
            || scheduled.NextAttempt != activation.Attempt
            || !string.Equals(scheduled.AttemptOperationId, activation.AttemptOperationId, StringComparison.Ordinal))
        {
            return Result(GovernedLoopRetryExecutionStatus.Conflict, run, scheduled, "retry-waiting-activation-conflict");
        }

        GovernedLoopSleepPublicationResult publication;
        try
        {
            publication = await _sleep.PublishAsync(
                new GovernedLoopSleepPublicationRequest(
                    new GovernedLoopSleepBinding(
                        binding.ExecutionBinding,
                        binding.AdmissionReceipt.Intent.Publication,
                        run.Frontier!.Payload.FrontierVersion,
                        run.Frontier.Payload.ContentHash,
                        activation.ActivationOrdinal,
                        activation.CycleId,
                        activation.CycleIteration,
                        activation.NodeId,
                        activation.VisitOrdinal,
                        activation.Attempt!.Value,
                        activation.AttemptOperationId!),
                    GovernedLoopWakeMode.Timestamp,
                    scheduled.NextRetryAtUtc,
                    null,
                    scheduled.RecordedAtUtc),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result(GovernedLoopRetryExecutionStatus.Unavailable, run, scheduled, "retry-checkpoint-publication-unavailable");
        }
        if (publication.Status is not (GovernedLoopSleepPublicationStatus.Published or GovernedLoopSleepPublicationStatus.Replayed)
            || publication.Checkpoint is not { } checkpoint)
        {
            return publication.Status is GovernedLoopSleepPublicationStatus.Unavailable or GovernedLoopSleepPublicationStatus.Ambiguous
                ? Result(GovernedLoopRetryExecutionStatus.Scheduled, run, scheduled, "retry-checkpoint-recovery-pending")
                : Result(publication.Status == GovernedLoopSleepPublicationStatus.AmbiguousAttempt ? GovernedLoopRetryExecutionStatus.NeedsReview : GovernedLoopRetryExecutionStatus.Conflict, run, scheduled, "retry-checkpoint-publication-incomplete");
        }

        var now = Max(run.UpdatedAtUtc, checkpoint.PublishedAtUtc);
        var attached = GovernedLoopRetryContract.CreateState(
            scheduled.Identity,
            scheduled.StateVersion + 1,
            scheduled.Disposition,
            scheduled.CurrentAttempt,
            scheduled.CurrentAttemptOperationId,
            scheduled.NextAttempt,
            scheduled.AttemptOperationId,
            scheduled.Budget,
            scheduled.NextRetryAtUtc,
            checkpoint.CheckpointId,
            checkpoint.ContentHash,
            scheduled.FailureEvidenceId,
            scheduled.FailureEvidenceHash,
            now);
        var stateEvent = StateEvent(run, attached, "The durable sleep checkpoint was attached to the exact retry state.");
        var candidate = Append(run, now, [stateEvent]);
        var mutation = await UpdateAsync(run, candidate, current => HasState(current, attached), cancellationToken).ConfigureAwait(false);
        if (mutation.Status is RunMutationStatus.Unavailable or RunMutationStatus.Ambiguous)
        {
            return Result(GovernedLoopRetryExecutionStatus.Scheduled, mutation.Run ?? run, scheduled, "retry-checkpoint-attachment-recovery-pending");
        }
        return mutation.Status is RunMutationStatus.Committed or RunMutationStatus.Replayed && mutation.Run is not null
            ? Result(mutation.Status == RunMutationStatus.Replayed ? GovernedLoopRetryExecutionStatus.Replayed : GovernedLoopRetryExecutionStatus.Scheduled, mutation.Run, attached, "retry-checkpoint-attached")
            : Result(MapMutation(mutation.Status), mutation.Run ?? run, scheduled, "retry-checkpoint-attachment-incomplete");
    }

    private static bool TryValidateRequest(GovernedLoopRetryExecutionRequest? request)
        => request?.Anchor is not null
            && request.Plan is not null
            && request.Node is not null
            && request.Node.RetryPolicy is not null
            && request.Failure is not null
            && string.Equals(request.Node.NodeId, request.Failure.NodeId, StringComparison.Ordinal)
            && request.Node.Ordinal >= 0
            && request.Node.Ordinal < request.Plan.Nodes.Count
            && ReferenceEquals(request.Plan.Nodes[request.Node.Ordinal], request.Node)
            && GovernedLoopRetryContract.IsValid(request.Node.RetryPolicy)
            && GovernedLoopFailureEvidenceContract.IsValid(request.Failure)
            && request.Failure.RetrySafety == GovernedLoopFailureRetrySafety.RetryableWithExactIntent
            && CustomLoopArtifactIdentifier.IsValid(request.Actor);

    private static DateTimeOffset FindSeriesStart(CustomLoopRunRecord run, GovernedLoopFailureEvidence failure)
        => run.Events.Where(item => item.SequentialNodeEvidence is { Kind: CustomLoopSequentialNodeEvidenceKind.DispatchStarted } evidence
                && evidence.ActivationOrdinal == failure.ActivationOrdinal
                && evidence.VisitOrdinal == failure.VisitOrdinal
                && evidence.Attempt == 1)
            .Select(item => item.TimestampUtc)
            .DefaultIfEmpty(failure.ObservedAtUtc)
            .Min();

    private async Task<RunRead> ReadRunAsync(string runId, CancellationToken cancellationToken)
    {
        try
        {
            var run = await _runStore.GetAsync(runId, cancellationToken).ConfigureAwait(false);
            return run is null ? new RunRead(RunReadStatus.NotFound, null)
                : !CustomLoopRunValidator.Validate(run).IsValid ? new RunRead(RunReadStatus.Conflict, run)
                : new RunRead(RunReadStatus.Found, run);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new RunRead(RunReadStatus.Unavailable, null);
        }
    }

    private async Task<RunMutation> UpdateAsync(CustomLoopRunRecord current, CustomLoopRunRecord candidate, Func<CustomLoopRunRecord, bool> exact, CancellationToken cancellationToken)
    {
        if (!CustomLoopRunValidator.ValidateUpdate(current, candidate).IsValid)
        {
            return new RunMutation(RunMutationStatus.Conflict, current);
        }
        CustomLoopRunStoreResult? stored = null;
        try
        {
            stored = await _runStore.UpdateAsync(candidate, current.LifecycleVersion, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The store boundary may already have committed; reconcile below without the caller token.
        }
        catch
        {
        }
        if (stored?.Status == CustomLoopRunStoreStatus.Updated && stored.Run is { } updated && exact(updated))
        {
            return new RunMutation(RunMutationStatus.Committed, updated);
        }
        var read = await ReadRunAsync(current.Id, CancellationToken.None).ConfigureAwait(false);
        if (read.Run is { } durable && exact(durable))
        {
            return new RunMutation(RunMutationStatus.Replayed, durable);
        }
        return stored?.Status switch
        {
            CustomLoopRunStoreStatus.NotFound => new RunMutation(RunMutationStatus.NotFound, null),
            CustomLoopRunStoreStatus.Conflict or CustomLoopRunStoreStatus.OperationConflict or CustomLoopRunStoreStatus.TerminalImmutable => new RunMutation(RunMutationStatus.Conflict, read.Run),
            _ when read.Status == RunReadStatus.Unavailable => new RunMutation(RunMutationStatus.Unavailable, read.Run),
            _ => new RunMutation(RunMutationStatus.Ambiguous, read.Run),
        };
    }

    private static CustomLoopRunRecord Append(CustomLoopRunRecord run, DateTimeOffset timestampUtc, IReadOnlyList<CustomLoopRunEvent> events)
        => run with { LifecycleVersion = checked(run.LifecycleVersion + 1), UpdatedAtUtc = timestampUtc, Events = [.. run.Events, .. events] };

    private static CustomLoopRunEvent StateEvent(CustomLoopRunRecord run, GovernedLoopRetryState state, string detail)
        => new(run.Events.Length + 1, $"retry-{state.Identity.SeriesId[..16]}-{state.StateVersion}", state.RecordedAtUtc, CustomLoopRunEventKind.RetryStateChanged, run.Frontier?.Payload.Nodes.ElementAtOrDefault(state.Identity.ActivationOrdinal)?.CycleIteration ?? run.Checkpoint.Iteration, state.Identity.NodeId, state.CurrentAttempt, detail, [], null, null, null, null, null, null, null, null, null, null)
        {
            RetryState = state,
        };

    private static CustomLoopRunEvent LifecycleEvent(CustomLoopRunRecord run, DateTimeOffset timestampUtc, string detail)
        => new(run.Events.Length + 1, $"retry-lifecycle-{Guid.NewGuid():N}", timestampUtc, CustomLoopRunEventKind.LifecycleChanged, null, null, null, detail, [], null, null, null, null, null, null, null, null, null, null);

    private static CustomLoopExecutionClock StopClock(CustomLoopExecutionClock clock, DateTimeOffset timestampUtc)
        => clock.ActiveSinceUtc is { } active ? clock with { AccumulatedRunningMilliseconds = checked(clock.AccumulatedRunningMilliseconds + Math.Max(0, (long)(timestampUtc - active).TotalMilliseconds)), ActiveSinceUtc = null } : clock;

    private static GovernedLoopRetryState? LatestRetry(
        CustomLoopRunRecord run,
        int? activationOrdinal = null,
        int? visitOrdinal = null,
        string? seriesId = null)
        => run.Events.Select(item => item.RetryState)
            .Where(state => state is not null
                && (activationOrdinal is null || state.Identity.ActivationOrdinal == activationOrdinal)
                && (visitOrdinal is null || state.Identity.VisitOrdinal == visitOrdinal)
                && (seriesId is null || string.Equals(state.Identity.SeriesId, seriesId, StringComparison.Ordinal)))
            .OrderByDescending(state => state!.StateVersion)
            .ThenByDescending(state => state!.RecordedAtUtc)
            .FirstOrDefault();

    private static IReadOnlyList<GovernedLoopRetryState> LatestRetries(CustomLoopRunRecord run)
        => run.Events.Select(item => item.RetryState)
            .Where(state => state is not null)
            .Cast<GovernedLoopRetryState>()
            .GroupBy(state => state.Identity.SeriesId, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(state => state.StateVersion).ThenByDescending(state => state.RecordedAtUtc).First())
            .ToArray();

    private static RetryCheckpointSelection? FindRetryForCheckpoint(CustomLoopRunRecord run, GovernedLoopSleepCheckpoint checkpoint)
    {
        var states = run.Events.Select(item => item.RetryState)
            .Where(state => state is not null)
            .Cast<GovernedLoopRetryState>()
            .ToArray();
        var matching = states.Where(state => SameCheckpointBinding(state, checkpoint)).ToArray();
        var seriesIds = matching.Select(state => state.Identity.SeriesId).Distinct(StringComparer.Ordinal).Take(2).ToArray();
        if (seriesIds.Length != 1)
        {
            return null;
        }

        var series = states.Where(state => string.Equals(state.Identity.SeriesId, seriesIds[0], StringComparison.Ordinal))
            .OrderBy(state => state.StateVersion)
            .ThenBy(state => state.RecordedAtUtc)
            .ToArray();
        var latest = series.Last();
        var dispatchWasCommitted = series.Any(state => state.Disposition == GovernedLoopRetryStateDisposition.Dispatched && SameCheckpointBinding(state, checkpoint));
        return new RetryCheckpointSelection(latest, dispatchWasCommitted);
    }

    private static bool SameCheckpointBinding(GovernedLoopRetryState state, GovernedLoopSleepCheckpoint checkpoint)
        => string.Equals(state.WakeCheckpointId, checkpoint.CheckpointId, StringComparison.Ordinal)
            && string.Equals(state.WakeCheckpointHash, checkpoint.ContentHash, StringComparison.Ordinal)
            && state.Identity.ActivationOrdinal == checkpoint.Binding.ActivationOrdinal
            && state.Identity.VisitOrdinal == checkpoint.Binding.NodeVisitOrdinal
            && string.Equals(state.Identity.NodeId, checkpoint.Binding.NodeId, StringComparison.Ordinal)
            && state.NextAttempt == checkpoint.Binding.WaitAttempt
            && string.Equals(state.AttemptOperationId, checkpoint.Binding.WaitOperationId, StringComparison.Ordinal);

    private static GovernedLoopFailureEvidence? FindFailure(CustomLoopRunRecord run, GovernedLoopRetryState state)
        => FindFailureEvent(run, state)?.FailureEvidence;

    private static CustomLoopRunEvent? FindFailureEvent(CustomLoopRunRecord run, GovernedLoopRetryState state)
    {
        var matches = run.Events.Where(item => item.FailureEvidence is { } failure
            && string.Equals(failure.EvidenceId, state.FailureEvidenceId, StringComparison.Ordinal)
            && string.Equals(failure.ContentHash, state.FailureEvidenceHash, StringComparison.Ordinal)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool HasPotentialRecoveryWork(CustomLoopRunRecord? run)
    {
        try
        {
            return run?.Events?.Any(item => item?.RetryState is { Disposition: GovernedLoopRetryStateDisposition.Scheduled, WakeCheckpointId: null }) == true;
        }
        catch
        {
            return true;
        }
    }

    private static bool HasState(CustomLoopRunRecord run, GovernedLoopRetryState expected)
        => run.Events.Count(item => string.Equals(item.RetryState?.ContentHash, expected.ContentHash, StringComparison.Ordinal)) == 1;

    private static DateTimeOffset? FindCycleStartedAtUtc(CustomLoopRunRecord run, string? cycleId)
        => cycleId is null
            ? null
            : run.Events
                .Where(item => string.Equals(item.SequentialNodeEvidence?.CycleId, cycleId, StringComparison.Ordinal)
                    && item.SequentialNodeEvidence?.CycleIteration == 1)
                .Select(item => (DateTimeOffset?)item.TimestampUtc)
                .Min();

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left >= right ? left : right;

    private static GovernedLoopRetryExecutionResult Result(GovernedLoopRetryExecutionStatus status, CustomLoopRunRecord? run = null, GovernedLoopRetryState? state = null, string detail = "retry-outcome-unavailable")
        => new(status, run, state, detail);

    private static GovernedLoopRetryExecutionStatus MapDecision(GovernedLoopRetryDecisionStatus status)
        => status switch
        {
            GovernedLoopRetryDecisionStatus.Exhausted => GovernedLoopRetryExecutionStatus.Exhausted,
            GovernedLoopRetryDecisionStatus.NoRetry or GovernedLoopRetryDecisionStatus.Ineligible or GovernedLoopRetryDecisionStatus.Cancelled or GovernedLoopRetryDecisionStatus.Paused => GovernedLoopRetryExecutionStatus.Ineligible,
            GovernedLoopRetryDecisionStatus.NeedsReview => GovernedLoopRetryExecutionStatus.NeedsReview,
            _ => GovernedLoopRetryExecutionStatus.Conflict,
        };

    private static GovernedLoopRetryExecutionStatus MapRead(RetryRunReadStatus status)
        => status == RetryRunReadStatus.Unavailable ? GovernedLoopRetryExecutionStatus.Unavailable : GovernedLoopRetryExecutionStatus.Conflict;

    private static GovernedLoopRetryExecutionStatus MapMutation(RetryRunMutationStatus status)
        => status switch
        {
            RetryRunMutationStatus.Unavailable => GovernedLoopRetryExecutionStatus.Unavailable,
            RetryRunMutationStatus.Ambiguous => GovernedLoopRetryExecutionStatus.NeedsReview,
            _ => GovernedLoopRetryExecutionStatus.Conflict,
        };

    private sealed record RunRead(RetryRunReadStatus Status, CustomLoopRunRecord? Run);
    private sealed record RunMutation(RetryRunMutationStatus Status, CustomLoopRunRecord? Run);
    private sealed record RetryRecoveryCandidate(string RunId, string SeriesId, DateTimeOffset RecordedAtUtc);
    private sealed record RetryCheckpointSelection(GovernedLoopRetryState Latest, bool DispatchWasCommitted);
}
