using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Application.Loops.Wait.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Common.Loops.Execution.Wait;
using EmbodySense.Core.Common.Loops.Execution.Wait.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Sequential.Models;

namespace EmbodySense.Core.Application.Loops.Wait;

/// <summary>Executes admitted Wait nodes directly over the canonical run artifact and durable sleep/wake substrate.</summary>
/// <remarks>
/// The canonical run remains the only lifecycle, frontier, and Wait-evidence truth. Park, checkpoint attachment,
/// continuation, and ordered completion are separate optimistic phases so every crash boundary can be reconciled.
/// </remarks>
public sealed class GovernedLoopWaitExecutionService : IGovernedLoopWaitNodeExecutor, IGovernedLoopWakeContinuationPort, IGovernedLoopWaitRecoveryPort
{
    private readonly ICustomLoopRunStore _runStore;
    private readonly GovernedLoopSleepService _sleepService;
    private readonly IGovernedLoopSleepCurrentPosturePort _currentPosture;
    private readonly ICapabilityAuthorityTransaction _authorityTransaction;
    private readonly ICustomLoopWorkspaceExecutionGate _executionGate;
    private readonly IGovernedLoopWaitOrderedResumePort _orderedResume;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates one adapter-independent executable Wait coordinator over the canonical run store.</summary>
    public GovernedLoopWaitExecutionService(
        ICustomLoopRunStore runStore,
        GovernedLoopSleepService sleepService,
        IGovernedLoopSleepCurrentPosturePort currentPosture,
        ICapabilityAuthorityTransaction authorityTransaction,
        ICustomLoopWorkspaceExecutionGate executionGate,
        IGovernedLoopWaitOrderedResumePort orderedResume,
        TimeProvider? timeProvider = null)
    {
        _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
        _sleepService = sleepService ?? throw new ArgumentNullException(nameof(sleepService));
        _currentPosture = currentPosture ?? throw new ArgumentNullException(nameof(currentPosture));
        _authorityTransaction = authorityTransaction ?? throw new ArgumentNullException(nameof(authorityTransaction));
        _executionGate = executionGate ?? throw new ArgumentNullException(nameof(executionGate));
        _orderedResume = orderedResume ?? throw new ArgumentNullException(nameof(orderedResume));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<GovernedLoopWaitParkResult> ParkAsync(
        GovernedLoopSequentialNodeDispatchRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsExactRunningWaitRequest(request)
            || !TryCreateCondition(request, out var condition))
        {
            return Park(GovernedLoopWaitParkResultStatus.Invalid, detail: "invalid-wait-dispatch");
        }

        var read = await ReadRunAsync(request.Anchor.AdapterBinding.ExecutionBinding, cancellationToken).ConfigureAwait(false);
        if (read.Status != RunReadStatus.Found)
        {
            return Park(MapReadToPark(read.Status), run: read.Run, detail: "canonical-run-read-failed");
        }

        var run = read.Run!;
        if (!MatchesRequest(run, request))
        {
            return Park(GovernedLoopWaitParkResultStatus.Conflict, run: run, detail: "wait-dispatch-substituted");
        }

        var retained = Wait(run, request.Activation.ActivationOrdinal);
        if (retained is not null)
        {
            if (!SameCondition(retained.Condition, condition))
            {
                return Park(GovernedLoopWaitParkResultStatus.Conflict, run: run, detail: "wait-condition-substituted");
            }

            if (retained.ParkEvidence is { } replayed)
            {
                return Park(GovernedLoopWaitParkResultStatus.Replayed, replayed, run, "wait-checkpoint-replayed");
            }

            if (run.Frontier!.Payload.Nodes[retained.ActivationOrdinal].Status != GovernedLoopNodeExecutionStatus.Waiting)
            {
                return Park(GovernedLoopWaitParkResultStatus.Conflict, run: run, detail: "wait-evidence-phase-conflict");
            }

            return await PublishCheckpointAsync(run, retained, cancellationToken).ConfigureAwait(false);
        }

        if (!TryReadUtcNow(run.UpdatedAtUtc, out var parkedAtUtc))
        {
            return Park(GovernedLoopWaitParkResultStatus.Unavailable, run: run, detail: "wait-time-unavailable");
        }

        var activation = run.Frontier!.Payload.Nodes[request.Activation.ActivationOrdinal];
        var transition = GovernedLoopSequentialFrontierMachine.ParkRunning(
            run.Frontier,
            request.Anchor.AdapterBinding,
            request.Plan,
            request.Node,
            activation,
            request.Attempt,
            activation.AttemptOperationId,
            parkedAtUtc);
        if (transition.Status != GovernedLoopSequentialFrontierTransitionStatus.Applied || transition.Frontier is null)
        {
            return Park(GovernedLoopWaitParkResultStatus.Conflict, run: run, detail: "wait-frontier-park-conflict");
        }

        var evidence = GovernedLoopWaitContractHash.Apply(new GovernedLoopWaitExecutionEvidence(
            GovernedLoopWaitExecutionEvidence.CurrentSchemaVersion,
            activation.ActivationOrdinal,
            activation.NodeId,
            activation.VisitOrdinal,
            activation.CycleId,
            activation.CycleIteration,
            request.Attempt,
            activation.AttemptOperationId!,
            condition,
            parkedAtUtc,
            transition.Frontier.Payload.FrontierVersion,
            transition.Frontier.Payload.ContentHash,
            null,
            null,
            string.Empty));
        if (!GovernedLoopWaitContractValidator.Validate(evidence).IsValid)
        {
            return Park(GovernedLoopWaitParkResultStatus.Invalid, run: run, detail: "wait-evidence-invalid");
        }

        var aggregateWaiting = transition.Frontier.Payload.Status == GovernedLoopFrontierStatus.Waiting;
        var events = Array.Empty<CustomLoopRunEvent>();
        if (aggregateWaiting)
        {
            events = [LifecycleEvent(run, parkedAtUtc, "Ordered execution entered Waiting after the exact Wait frontier became durable.")];
        }

        var candidate = Append(run, parkedAtUtc, events) with
        {
            Status = aggregateWaiting ? CustomLoopRunStatus.Waiting : CustomLoopRunStatus.Running,
            ExecutionClock = aggregateWaiting ? StopClock(run.ExecutionClock, parkedAtUtc) : run.ExecutionClock,
            Frontier = transition.Frontier,
            WaitEvidence = [.. run.WaitEvidence, evidence],
        };
        var mutation = await UpdateAsync(
            run,
            candidate,
            cancellationToken,
            current => IsParkedWith(current, evidence)).ConfigureAwait(false);
        if (mutation.Status is not (RunMutationStatus.Committed or RunMutationStatus.Replayed)
            || Wait(mutation.Run, evidence.ActivationOrdinal) is not { } parked)
        {
            return Park(MapMutationToPark(mutation.Status), run: mutation.Run ?? run, detail: "wait-park-cas-failed");
        }

        return await PublishCheckpointAsync(mutation.Run!, parked, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Recovers bounded unpublished parks and committed-but-incomplete continuations after restart.</summary>
    public async Task<GovernedLoopWaitRecoveryResult> RecoverAsync(
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (maximumCount is < 1 or > 256)
        {
            return new GovernedLoopWaitRecoveryResult(0, 0, 0);
        }

        IReadOnlyList<CustomLoopRunRecord> candidates;
        try
        {
            candidates = await _runStore.ListNonterminalAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new GovernedLoopWaitRecoveryResult(0, 0, 1);
        }

        if (candidates is null
            || candidates.Any(candidate => candidate is null)
            || candidates.Select(candidate => candidate.Id).Distinct(StringComparer.Ordinal).Count() != candidates.Count)
        {
            return new GovernedLoopWaitRecoveryResult(0, 0, 1);
        }

        var invalidCandidateCount = candidates.Count(candidate =>
            HasPotentialWaitRecoveryWork(candidate)
            && !IsRecoveryCandidate(candidate));
        var selected = candidates
            .Where(IsRecoveryCandidate)
            .Where(HasRecoveryWork)
            .OrderBy(candidate => candidate.UpdatedAtUtc)
            .ThenBy(candidate => candidate.Id, StringComparer.Ordinal)
            .Take(maximumCount)
            .ToArray();
        var recovered = 0;
        var needsReview = invalidCandidateCount;
        foreach (var candidate in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var activationOrdinals = candidate.WaitEvidence
                .Select(item => item.ActivationOrdinal)
                .Concat(GovernedLoopWaitClaimEvidence.FindExactRecoverableClaims(candidate).Select(item => item.ActivationOrdinal))
                .Distinct()
                .ToArray();
            foreach (var activationOrdinal in activationOrdinals)
            {
                var read = await ReadRunAsync(candidate.SequentialAdapterBinding!.ExecutionBinding, cancellationToken).ConfigureAwait(false);
                var wait = read.Status == RunReadStatus.Found ? Wait(read.Run, activationOrdinal) : null;
                if (read.Run is not { } run)
                {
                    needsReview++;
                    continue;
                }

                var activation = run.Frontier!.Payload.Nodes.ElementAtOrDefault(activationOrdinal);
                if (wait is null
                    && activation is not null
                    && GovernedLoopWaitClaimEvidence.FindExactRecoverableClaims(run)
                        .Any(item => item.ActivationOrdinal == activationOrdinal))
                {
                    var claim = await RecoverClaimAsync(run, activation, cancellationToken).ConfigureAwait(false);
                    if (claim.Status is GovernedLoopWaitParkResultStatus.Parked or GovernedLoopWaitParkResultStatus.Replayed)
                    {
                        recovered++;
                    }
                    else if (claim.Run?.Status is CustomLoopRunStatus.Cancelled or CustomLoopRunStatus.Failed)
                    {
                        recovered++;
                    }
                    else if (claim.Run?.Status == CustomLoopRunStatus.NeedsReview)
                    {
                        needsReview++;
                    }
                    else
                    {
                        needsReview++;
                    }

                    continue;
                }

                if (wait is null)
                {
                    needsReview++;
                    continue;
                }

                if (activation?.Status == GovernedLoopNodeExecutionStatus.Waiting && wait.ParkEvidence is null)
                {
                    var publication = await PublishCheckpointAsync(run, wait, cancellationToken).ConfigureAwait(false);
                    if (publication.Status is GovernedLoopWaitParkResultStatus.Parked or GovernedLoopWaitParkResultStatus.Replayed)
                    {
                        recovered++;
                    }
                    else if (publication.Run?.Status is CustomLoopRunStatus.Cancelled or CustomLoopRunStatus.Failed)
                    {
                        recovered++;
                    }
                    else
                    {
                        needsReview++;
                    }

                    continue;
                }

                if (activation?.Status == GovernedLoopNodeExecutionStatus.Running
                    && wait.ParkEvidence is { } park
                    && wait.ContinuationEvidence is { } continuation)
                {
                    var result = await ContinueCoreAsync(
                        new GovernedLoopWakeContinuationRequest(
                            park.Checkpoint,
                            continuation.PreparedWakeEvidence.Identity,
                            continuation.PreparedWakeEvidence.ContinuationOperationId!,
                            continuation.PreparedWakeEvidence,
                            null),
                        reconcileOnly: true,
                        cancellationToken).ConfigureAwait(false);
                    if (result?.Status == GovernedLoopWakeContinuationStatus.Committed)
                    {
                        recovered++;
                    }
                    else
                    {
                        needsReview++;
                    }
                }
            }
        }

        return new GovernedLoopWaitRecoveryResult(selected.Length, recovered, needsReview);
    }

    private async Task<GovernedLoopWaitParkResult> RecoverClaimAsync(
        CustomLoopRunRecord run,
        GovernedLoopNodeExecutionEvidence activation,
        CancellationToken cancellationToken)
    {
        GovernedLoopWaitOrderedContext? context;
        try
        {
            context = await _orderedResume.ResolveAsync(run, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Park(GovernedLoopWaitParkResultStatus.Unavailable, run: run, detail: "wait-claim-context-unavailable");
        }

        if (context is null
            || !SamePlanBinding(run, context)
            || activation.PlanOrdinal < 0
            || activation.PlanOrdinal >= context.Plan.Nodes.Count
            || activation.Attempt is not { } attempt)
        {
            return Park(GovernedLoopWaitParkResultStatus.Conflict, run: run, detail: "wait-claim-context-conflict");
        }

        var node = context.Plan.Nodes[activation.PlanOrdinal];
        return await ParkAsync(
            new GovernedLoopSequentialNodeDispatchRequest(
                GovernedLoopSequentialNodeDispatchRequest.CurrentSchemaVersion,
                context.Anchor,
                context.Plan,
                node,
                activation,
                attempt),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<GovernedLoopWakeContinuationResult?> ContinueCoreAsync(
        GovernedLoopWakeContinuationRequest? request,
        bool reconcileOnly,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsValidContinuationRequest(request, reconcileOnly))
        {
            return Continuation(GovernedLoopWakeContinuationStatus.Conflict, reference: "invalid-continuation-request");
        }

        var initialRead = await ReadRunAsync(request!.Checkpoint.Binding.Execution, cancellationToken).ConfigureAwait(false);
        if (initialRead.Status != RunReadStatus.Found)
        {
            return Continuation(MapReadToContinuation(initialRead.Status), reference: "canonical-run-read-failed");
        }

        var initialWait = Wait(initialRead.Run, request.Checkpoint.Binding.ActivationOrdinal);
        var prepared = request.PreparedWakeEvidence ?? initialWait?.ContinuationEvidence?.PreparedWakeEvidence;
        if (prepared is null)
        {
            return reconcileOnly
                ? Continuation(GovernedLoopWakeContinuationStatus.NotCommitted, reference: "continuation-not-found")
                : Continuation(GovernedLoopWakeContinuationStatus.Conflict, reference: "prepared-wake-required");
        }

        if (!MatchesPreparedWake(prepared, request))
        {
            return Continuation(GovernedLoopWakeContinuationStatus.Conflict, reference: "prepared-wake-substituted");
        }

        CustomLoopExecutionLeaseResult ownership;
        try
        {
            ownership = _executionGate.TryAcquire(request.ContinuationOperationId, prepared.ContentHash);
        }
        catch
        {
            return Continuation(GovernedLoopWakeContinuationStatus.Unavailable, reference: "workspace-gate-unavailable");
        }

        if (ownership.Status != CustomLoopExecutionLeaseStatus.Acquired || ownership.Lease is null)
        {
            return ownership.Status switch
            {
                CustomLoopExecutionLeaseStatus.OperationConflict
                    => Continuation(GovernedLoopWakeContinuationStatus.Conflict, reference: "workspace-operation-conflict"),
                CustomLoopExecutionLeaseStatus.WorkspaceBusy
                    => Continuation(GovernedLoopWakeContinuationStatus.Unavailable, reference: "workspace-execution-busy"),
                CustomLoopExecutionLeaseStatus.WorkspaceHostUnavailable
                    => Continuation(GovernedLoopWakeContinuationStatus.Unavailable, reference: "workspace-host-unavailable"),
                CustomLoopExecutionLeaseStatus.OperationInProgress
                    => Continuation(GovernedLoopWakeContinuationStatus.Unavailable, reference: "workspace-operation-in-progress"),
                _ => Continuation(GovernedLoopWakeContinuationStatus.Unavailable, reference: "workspace-ownership-unavailable"),
            };
        }

        using (ownership.Lease)
        {
            ContinuationPreparation preparation;
            try
            {
                preparation = await _authorityTransaction.ExecuteAsync(
                    token => PrepareContinuationAsync(request, prepared, reconcileOnly, token),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return Continuation(GovernedLoopWakeContinuationStatus.Unavailable, reference: "continuation-authority-unavailable");
            }

            if (preparation.Terminal is not null)
            {
                return preparation.Terminal;
            }

            var run = preparation.Run!;
            var wait = Wait(run, request.Checkpoint.Binding.ActivationOrdinal)!;
            var continuation = wait.ContinuationEvidence!;
            CustomLoopOrderedRunResult resumed;
            try
            {
                resumed = await _orderedResume.ResumeAsync(
                    new GovernedLoopWaitOrderedResumeRequest(
                        preparation.Context!,
                        wait.ActivationOrdinal,
                        continuation.ContentHash,
                        run.AdmissionActor),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                resumed = null!;
            }

            var completedRead = await ReadRunAsync(request.Checkpoint.Binding.Execution, CancellationToken.None).ConfigureAwait(false);
            var completedWait = completedRead.Status == RunReadStatus.Found
                ? Wait(completedRead.Run, wait.ActivationOrdinal)
                : null;
            if (completedWait?.ContinuationEvidence is { } durableContinuation
                && string.Equals(durableContinuation.ContentHash, continuation.ContentHash, StringComparison.Ordinal)
                && IsCompletedWith(completedRead.Run!, wait.ActivationOrdinal, continuation.ContentHash)
                && resumed is not null
                && resumed.Status is CustomLoopOrderedRunStatus.Completed or CustomLoopOrderedRunStatus.Waiting)
            {
                return Continuation(GovernedLoopWakeContinuationStatus.Committed, continuation.ContentHash);
            }

            return resumed is null || resumed.Status is CustomLoopOrderedRunStatus.Failed or CustomLoopOrderedRunStatus.NeedsReview
                ? Continuation(GovernedLoopWakeContinuationStatus.Ambiguous, reference: "ordered-wait-reentry-incomplete")
                : Continuation(GovernedLoopWakeContinuationStatus.Conflict, reference: "wait-completion-substituted");
        }
    }

    private async Task<ContinuationPreparation> PrepareContinuationAsync(
        GovernedLoopWakeContinuationRequest request,
        GovernedLoopWakeEvidence prepared,
        bool reconcileOnly,
        CancellationToken cancellationToken)
    {
        var read = await ReadRunAsync(request.Checkpoint.Binding.Execution, cancellationToken).ConfigureAwait(false);
        if (read.Status != RunReadStatus.Found || read.Run is not { } run)
        {
            return Preparation(Continuation(MapReadToContinuation(read.Status), reference: "canonical-run-read-failed"));
        }

        var wait = Wait(run, request.Checkpoint.Binding.ActivationOrdinal);
        if (wait is null)
        {
            return Preparation(Continuation(GovernedLoopWakeContinuationStatus.Conflict, reference: "wait-evidence-not-found"));
        }

        if (!MatchesCheckpoint(wait, request.Checkpoint))
        {
            if (wait.ParkEvidence is not null || !TryCreateParkEvidence(run, wait, request.Checkpoint, out var parkEvidence))
            {
                return Preparation(Continuation(GovernedLoopWakeContinuationStatus.Conflict, reference: "checkpoint-substituted"));
            }

            var attached = wait with
            {
                ParkEvidence = parkEvidence,
                ContentHash = string.Empty,
            };
            attached = GovernedLoopWaitContractHash.Apply(attached);
            var attachCandidate = Append(run, Max(run.UpdatedAtUtc, request.Checkpoint.PublishedAtUtc), []) with
            {
                WaitEvidence = ReplaceWait(run.WaitEvidence, attached),
            };
            var attachment = await UpdateAsync(
                run,
                attachCandidate,
                cancellationToken,
                current => string.Equals(Wait(current, wait.ActivationOrdinal)?.ParkEvidence?.ContentHash, parkEvidence.ContentHash, StringComparison.Ordinal)).ConfigureAwait(false);
            if (attachment.Status is not (RunMutationStatus.Committed or RunMutationStatus.Replayed) || attachment.Run is null)
            {
                return Preparation(Continuation(
                    attachment.Status == RunMutationStatus.Conflict ? GovernedLoopWakeContinuationStatus.Conflict : GovernedLoopWakeContinuationStatus.Ambiguous,
                    reference: "checkpoint-attachment-incomplete"));
            }

            run = attachment.Run;
            wait = Wait(run, wait.ActivationOrdinal)!;
        }

        if (wait.ContinuationEvidence is { } retainedContinuation)
        {
            if (!MatchesPreparedWake(retainedContinuation.PreparedWakeEvidence, request))
            {
                return Preparation(Continuation(GovernedLoopWakeContinuationStatus.Conflict, reference: "continuation-substituted"));
            }

            var retainedContext = await _orderedResume.ResolveAsync(run, cancellationToken).ConfigureAwait(false);
            return retainedContext is null
                ? Preparation(Continuation(GovernedLoopWakeContinuationStatus.Unavailable, reference: "immutable-wait-context-unavailable"))
                : new ContinuationPreparation(run, retainedContext, null);
        }

        if (reconcileOnly)
        {
            return Preparation(Continuation(GovernedLoopWakeContinuationStatus.NotCommitted, reference: "continuation-not-found"));
        }

        if (request.ExpectedPostureHash is null)
        {
            return Preparation(Continuation(GovernedLoopWakeContinuationStatus.Conflict, reference: "continuation-posture-required"));
        }

        var postureStatus = await ReadExactPostureAsync(
            request.Checkpoint.Binding.Execution,
            request.Checkpoint,
            request.ExpectedPostureHash,
            cancellationToken).ConfigureAwait(false);
        if (postureStatus is not null)
        {
            return Preparation(postureStatus);
        }

        var context = await _orderedResume.ResolveAsync(run, cancellationToken).ConfigureAwait(false);
        var activation = run.Frontier!.Payload.Nodes.ElementAtOrDefault(wait.ActivationOrdinal);
        if (context is null
            || activation?.Status != GovernedLoopNodeExecutionStatus.Waiting
            || !SamePlanBinding(run, context))
        {
            return Preparation(Continuation(GovernedLoopWakeContinuationStatus.Conflict, reference: "waiting-frontier-or-context-conflict"));
        }

        if (!TryReadUtcNow(Max(run.UpdatedAtUtc, prepared.RecordedAtUtc), out var resumedAtUtc))
        {
            return Preparation(Continuation(GovernedLoopWakeContinuationStatus.Unavailable, reference: "continuation-time-unavailable"));
        }

        var resumed = GovernedLoopSequentialFrontierMachine.ResumeWaiting(
            run.Frontier,
            run.SequentialAdapterBinding!,
            context.Plan,
            activation,
            wait.WaitAttempt,
            wait.WaitOperationId,
            resumedAtUtc);
        if (resumed.Status != GovernedLoopSequentialFrontierTransitionStatus.Applied || resumed.Frontier is null)
        {
            return Preparation(Continuation(GovernedLoopWakeContinuationStatus.Conflict, reference: "waiting-frontier-conflict"));
        }

        var continuation = GovernedLoopWaitContractHash.Apply(new GovernedLoopWaitContinuationEvidence(
            GovernedLoopWaitContinuationEvidence.CurrentSchemaVersion,
            wait.ParkEvidence!.ContentHash,
            prepared,
            run.Frontier.Payload.FrontierVersion,
            run.Frontier.Payload.ContentHash,
            resumed.Frontier.Payload.FrontierVersion,
            resumed.Frontier.Payload.ContentHash,
            resumedAtUtc,
            string.Empty));
        if (!GovernedLoopWaitContractValidator.ValidateComposition(wait.ParkEvidence, continuation).IsValid)
        {
            return Preparation(Continuation(GovernedLoopWakeContinuationStatus.Conflict, reference: "continuation-evidence-invalid"));
        }

        var continuedWait = wait with
        {
            ContinuationEvidence = continuation,
            ContentHash = string.Empty,
        };
        continuedWait = GovernedLoopWaitContractHash.Apply(continuedWait);
        var lifecycleChanged = run.Status == CustomLoopRunStatus.Waiting;
        var events = lifecycleChanged
            ? new[] { LifecycleEvent(run, resumedAtUtc, "The exact prepared wake resumed ordered execution.", request.ContinuationOperationId) }
            : [];
        var candidate = Append(run, resumedAtUtc, events) with
        {
            Status = CustomLoopRunStatus.Running,
            ExecutionClock = lifecycleChanged ? run.ExecutionClock with { ActiveSinceUtc = resumedAtUtc } : run.ExecutionClock,
            Frontier = resumed.Frontier,
            WaitEvidence = ReplaceWait(run.WaitEvidence, continuedWait),
        };
        var mutation = await UpdateAsync(
            run,
            candidate,
            cancellationToken,
            current => HasExactContinuation(current, wait.ActivationOrdinal, continuation)).ConfigureAwait(false);
        if (mutation.Status is not (RunMutationStatus.Committed or RunMutationStatus.Replayed)
            || mutation.Run is null
            || !HasExactContinuation(mutation.Run, wait.ActivationOrdinal, continuation))
        {
            return Preparation(Continuation(
                mutation.Status == RunMutationStatus.Conflict ? GovernedLoopWakeContinuationStatus.Conflict : GovernedLoopWakeContinuationStatus.Ambiguous,
                reference: "continuation-cas-incomplete"));
        }

        return new ContinuationPreparation(mutation.Run, context, null);
    }

    private async Task<GovernedLoopWakeContinuationResult?> ReadExactPostureAsync(
        GovernedLoopExecutionBinding binding,
        GovernedLoopSleepCheckpoint checkpoint,
        string expectedPostureHash,
        CancellationToken cancellationToken)
    {
        if (!TryReadUtcNow(default, out var readStartedAtUtc))
        {
            return Continuation(GovernedLoopWakeContinuationStatus.Unavailable, reference: "posture-time-unavailable");
        }

        GovernedLoopSleepCurrentPostureReadResult? read;
        try
        {
            read = await _currentPosture.ReadAsync(binding, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Continuation(GovernedLoopWakeContinuationStatus.Unavailable, reference: "posture-read-unavailable");
        }

        if (!TryReadUtcNow(readStartedAtUtc, out var readCompletedAtUtc)
            || read?.Status != GovernedLoopSleepCurrentPostureReadStatus.Found
            || read.Posture is not { } posture
            || !GovernedLoopSleepPosturePolicy.IsWellFormed(posture, binding, readStartedAtUtc, readCompletedAtUtc)
            || !Equals(posture.Execution.Lifecycle.Binding, binding)
            || !string.Equals(posture.PostureHash, expectedPostureHash, StringComparison.Ordinal))
        {
            return Continuation(GovernedLoopWakeContinuationStatus.Unavailable, reference: "continuation-posture-stale");
        }

        return GovernedLoopSleepPosturePolicy.EvaluateWake(posture, checkpoint, readCompletedAtUtc) == GovernedLoopSleepPostureDecision.Eligible
            ? null
            : Continuation(GovernedLoopWakeContinuationStatus.Unavailable, reference: "continuation-posture-ineligible");
    }

    private async Task<GovernedLoopWaitParkResult> PublishCheckpointAsync(
        CustomLoopRunRecord run,
        GovernedLoopWaitExecutionEvidence wait,
        CancellationToken cancellationToken)
    {
        if (wait.ParkEvidence is { } replayed)
        {
            return Park(GovernedLoopWaitParkResultStatus.Replayed, replayed, run, "wait-checkpoint-replayed");
        }

        var activation = run.Frontier!.Payload.Nodes.ElementAtOrDefault(wait.ActivationOrdinal);
        if (activation?.Status != GovernedLoopNodeExecutionStatus.Waiting
            || run.SequentialAdapterBinding is not { } binding)
        {
            return Park(GovernedLoopWaitParkResultStatus.Conflict, run: run, detail: "waiting-activation-not-found");
        }

        var sleepBinding = new GovernedLoopSleepBinding(
            binding.ExecutionBinding,
            binding.AdmissionReceipt.Intent.Publication,
            wait.ParkedFrontierVersion,
            wait.ParkedFrontierHash,
            wait.ActivationOrdinal,
            wait.CycleId,
            wait.CycleIteration,
            wait.NodeId,
            wait.NodeVisitOrdinal,
            wait.WaitAttempt,
            wait.WaitOperationId);
        GovernedLoopSleepPublicationResult publication;
        try
        {
            publication = await _sleepService.PublishAsync(
                new GovernedLoopSleepPublicationRequest(
                    sleepBinding,
                    wait.Condition.WakeDeadlineUtc is null ? GovernedLoopWakeMode.AuthenticatedEvent : GovernedLoopWakeMode.Timestamp,
                    wait.Condition.WakeDeadlineUtc,
                    wait.Condition.AuthenticatedEventReference,
                    wait.ParkedAtUtc),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Park(GovernedLoopWaitParkResultStatus.Unavailable, run: run, detail: "sleep-publication-unavailable");
        }

        if (publication.Status is not (GovernedLoopSleepPublicationStatus.Published or GovernedLoopSleepPublicationStatus.Replayed)
            || publication.Checkpoint is null)
        {
            if (publication.Status is GovernedLoopSleepPublicationStatus.Cancelled
                or GovernedLoopSleepPublicationStatus.Expired
                or GovernedLoopSleepPublicationStatus.ReviewBlocked)
            {
                return await CommitDefinitivePublicationDispositionAsync(run, wait, publication.Status, cancellationToken).ConfigureAwait(false);
            }

            return Park(MapPublicationToPark(publication.Status), run: run, detail: "sleep-publication-incomplete");
        }

        if (!TryCreateParkEvidence(run, wait, publication.Checkpoint, out var parkEvidence))
        {
            return Park(GovernedLoopWaitParkResultStatus.Conflict, run: run, detail: "park-evidence-invalid");
        }

        var attached = wait with
        {
            ParkEvidence = parkEvidence,
            ContentHash = string.Empty,
        };
        attached = GovernedLoopWaitContractHash.Apply(attached);
        var updatedAtUtc = Max(run.UpdatedAtUtc, publication.Checkpoint.PublishedAtUtc);
        var candidate = Append(run, updatedAtUtc, []) with
        {
            WaitEvidence = ReplaceWait(run.WaitEvidence, attached),
        };
        var mutation = await UpdateAsync(
            run,
            candidate,
            cancellationToken,
            current => string.Equals(Wait(current, wait.ActivationOrdinal)?.ParkEvidence?.ContentHash, parkEvidence.ContentHash, StringComparison.Ordinal)).ConfigureAwait(false);
        return mutation.Status is RunMutationStatus.Committed or RunMutationStatus.Replayed
            && mutation.Run is not null
            && Wait(mutation.Run, wait.ActivationOrdinal)?.ParkEvidence is { } durable
            && string.Equals(durable.ContentHash, parkEvidence.ContentHash, StringComparison.Ordinal)
                ? Park(mutation.Status == RunMutationStatus.Replayed ? GovernedLoopWaitParkResultStatus.Replayed : GovernedLoopWaitParkResultStatus.Parked, durable, mutation.Run, "wait-checkpoint-attached")
                : Park(MapMutationToPark(mutation.Status), run: mutation.Run ?? run, detail: "checkpoint-attachment-incomplete");
    }

    private async Task<GovernedLoopWaitParkResult> CommitDefinitivePublicationDispositionAsync(
        CustomLoopRunRecord initial,
        GovernedLoopWaitExecutionEvidence wait,
        GovernedLoopSleepPublicationStatus publicationStatus,
        CancellationToken cancellationToken)
    {
        var targetStatus = publicationStatus switch
        {
            GovernedLoopSleepPublicationStatus.Cancelled => CustomLoopRunStatus.Cancelled,
            GovernedLoopSleepPublicationStatus.Expired => CustomLoopRunStatus.Failed,
            GovernedLoopSleepPublicationStatus.ReviewBlocked => CustomLoopRunStatus.NeedsReview,
            _ => CustomLoopRunStatus.Unknown,
        };
        if (targetStatus == CustomLoopRunStatus.Unknown)
        {
            return Park(GovernedLoopWaitParkResultStatus.Conflict, run: initial, detail: "sleep-publication-disposition-unsupported");
        }

        var current = initial;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (current.Status == targetStatus)
            {
                return Park(MapPublicationToPark(publicationStatus), run: current, detail: "sleep-publication-disposition-replayed");
            }

            var retained = Wait(current, wait.ActivationOrdinal);
            if (retained is null
                || !string.Equals(retained.ContentHash, wait.ContentHash, StringComparison.Ordinal)
                || current.SequentialAdapterBinding is not { } binding
                || current.Frontier?.Payload.Nodes.ElementAtOrDefault(wait.ActivationOrdinal)?.Status != GovernedLoopNodeExecutionStatus.Waiting
                || !TryReadUtcNow(current.UpdatedAtUtc, out var now))
            {
                return Park(GovernedLoopWaitParkResultStatus.Conflict, run: current, detail: "sleep-publication-disposition-conflict");
            }

            PublicationFailure? publicationFailure = null;
            if (targetStatus == CustomLoopRunStatus.Failed)
            {
                publicationFailure = await CreatePublicationFailureAsync(
                    current,
                    retained,
                    now,
                    "Checkpoint publication observed an expired admitted boundary; the canonical sleeping run failed without continuation.",
                    cancellationToken).ConfigureAwait(false);
                if (publicationFailure is null)
                {
                    return Park(GovernedLoopWaitParkResultStatus.Conflict, run: current, detail: "sleep-publication-failure-evidence-conflict");
                }
            }

            var frontierTransition = targetStatus switch
            {
                CustomLoopRunStatus.Cancelled => GovernedLoopSequentialFrontierMachine.CancelCurrent(current.Frontier, binding, now),
                CustomLoopRunStatus.Failed => GovernedLoopSequentialFrontierMachine.FailCurrent(
                    current.Frontier,
                    binding,
                    null,
                    publicationFailure!.Event.EventId,
                    publicationFailure.Event.SequentialNodeEvidence!.OutcomeArtifactHash,
                    GovernedLoopControlCondition.Failure,
                    publicationFailure.SelectedControlEdgeIds,
                    publicationFailure.SkippedControlEdgeIds,
                    now),
                CustomLoopRunStatus.NeedsReview => GovernedLoopSequentialFrontierMachine.ReviewBlockCurrent(current.Frontier, binding, null, null, now),
                _ => null,
            };
            if (frontierTransition?.Status != GovernedLoopSequentialFrontierTransitionStatus.Applied
                || frontierTransition.Frontier is null)
            {
                return Park(GovernedLoopWaitParkResultStatus.Conflict, run: current, detail: "sleep-publication-frontier-disposition-conflict");
            }

            var detail = publicationStatus switch
            {
                GovernedLoopSleepPublicationStatus.Cancelled => "Checkpoint publication observed a definitive cancellation posture; the canonical sleeping run was cancelled without continuation.",
                GovernedLoopSleepPublicationStatus.Expired => "Checkpoint publication observed an expired admitted boundary; the canonical sleeping run failed without continuation.",
                _ => "Checkpoint publication observed a definitive review posture; the canonical sleeping run was attention-blocked without continuation.",
            };
            var terminalEvents = publicationFailure is null
                ? Array.Empty<CustomLoopRunEvent>()
                : new[] { publicationFailure.Event };
            var lifecycleOwner = terminalEvents.Length == 0
                ? current
                : current with { Events = [.. current.Events, .. terminalEvents] };
            var lifecycle = LifecycleEvent(lifecycleOwner, now, detail);
            var candidate = Append(current, now, [.. terminalEvents, lifecycle]) with
            {
                Status = targetStatus,
                CompletedAtUtc = targetStatus is CustomLoopRunStatus.Cancelled or CustomLoopRunStatus.Failed or CustomLoopRunStatus.NeedsReview ? now : null,
                ExecutionClock = StopClock(current.ExecutionClock, now),
                FailureCode = targetStatus switch
                {
                    CustomLoopRunStatus.Failed => "wait_checkpoint_publication_expired",
                    CustomLoopRunStatus.NeedsReview => "wait_checkpoint_publication_review_blocked",
                    _ => null,
                },
                FailureDetail = targetStatus is CustomLoopRunStatus.Failed or CustomLoopRunStatus.NeedsReview ? detail : null,
                Frontier = frontierTransition.Frontier,
            };
            var mutation = await UpdateAsync(
                current,
                candidate,
                cancellationToken,
                retainedRun => IsExactDefinitivePublicationDisposition(retainedRun, candidate, current.Events.Length, wait)).ConfigureAwait(false);
            if ((mutation.Status is RunMutationStatus.Committed or RunMutationStatus.Replayed)
                && mutation.Run is not null)
            {
                return Park(MapPublicationToPark(publicationStatus), run: mutation.Run, detail: "sleep-publication-disposition-committed");
            }

            if (mutation.Run is not { } successor)
            {
                return Park(MapMutationToPark(mutation.Status), run: current, detail: "sleep-publication-disposition-cas-failed");
            }

            if (successor.IsTerminal)
            {
                return Park(MapMutationToPark(mutation.Status), run: successor, detail: "sleep-publication-disposition-cas-failed");
            }

            current = successor;
        }

        return Park(GovernedLoopWaitParkResultStatus.Conflict, run: current, detail: "sleep-publication-disposition-retry-exhausted");
    }

    private static bool IsExactDefinitivePublicationDisposition(
        CustomLoopRunRecord actual,
        CustomLoopRunRecord expected,
        int priorEventCount,
        GovernedLoopWaitExecutionEvidence wait)
    {
        if (actual.LifecycleVersion != expected.LifecycleVersion
            || actual.Status != expected.Status
            || actual.UpdatedAtUtc != expected.UpdatedAtUtc
            || actual.CompletedAtUtc != expected.CompletedAtUtc
            || actual.ExecutionClock != expected.ExecutionClock
            || !string.Equals(actual.FailureCode, expected.FailureCode, StringComparison.Ordinal)
            || !string.Equals(actual.FailureDetail, expected.FailureDetail, StringComparison.Ordinal)
            || !string.Equals(actual.Frontier?.Payload.ContentHash, expected.Frontier?.Payload.ContentHash, StringComparison.Ordinal)
            || !string.Equals(Wait(actual, wait.ActivationOrdinal)?.ContentHash, wait.ContentHash, StringComparison.Ordinal)
            || actual.Events.Length != expected.Events.Length
            || priorEventCount < 0
            || priorEventCount >= expected.Events.Length)
        {
            return false;
        }

        for (var index = priorEventCount; index < expected.Events.Length; index++)
        {
            var actualEvent = actual.Events[index];
            var expectedEvent = expected.Events[index];
            if (!string.Equals(
                    CustomLoopSequentialOutcomeArtifactHash.Compute(actualEvent),
                    CustomLoopSequentialOutcomeArtifactHash.Compute(expectedEvent),
                    StringComparison.Ordinal)
                || !string.Equals(
                    actualEvent.SequentialNodeEvidence?.EvidenceHash,
                    expectedEvent.SequentialNodeEvidence?.EvidenceHash,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<PublicationFailure?> CreatePublicationFailureAsync(
        CustomLoopRunRecord run,
        GovernedLoopWaitExecutionEvidence wait,
        DateTimeOffset timestampUtc,
        string detail,
        CancellationToken cancellationToken)
    {
        GovernedLoopWaitOrderedContext? context;
        try
        {
            context = await _orderedResume.ResolveAsync(run, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }

        if (context is null
            || !SamePlanBinding(run, context)
            || run.SequentialAdapterBinding is not { } binding
            || run.Frontier?.Payload.Nodes.ElementAtOrDefault(wait.ActivationOrdinal) is not { } activation
            || activation.Status != GovernedLoopNodeExecutionStatus.Waiting
            || activation.Attempt is not { } attempt
            || activation.PlanOrdinal < 0
            || activation.PlanOrdinal >= context.Plan.Nodes.Count
            || context.Plan.Nodes[activation.PlanOrdinal] is not { } node
            || !string.Equals(node.NodeId, activation.NodeId, StringComparison.Ordinal))
        {
            return null;
        }

        var selected = context.Plan.ControlEdges
            .Where(edge => activation.OutgoingControlEdgeIds.Contains(edge.Id, StringComparer.Ordinal)
                && edge.Condition == GovernedLoopControlCondition.Failure)
            .Select(edge => edge.Id)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var skipped = activation.OutgoingControlEdgeIds
            .Except(selected, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var runEvent = new CustomLoopRunEvent(
            run.Events.Length + 1,
            $"wait-publication-{Guid.NewGuid():N}",
            timestampUtc,
            CustomLoopRunEventKind.NodeAttemptFailed,
            activation.CycleIteration ?? run.Checkpoint.Iteration,
            activation.NodeId,
            attempt,
            detail,
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
            PureNodeOutcomeJson = null,
            WaitContinuationEvidenceHash = null,
        };
        var evidence = CustomLoopSequentialNodeEvidenceHash.Apply(new CustomLoopSequentialNodeEvidence(
            CustomLoopSequentialNodeEvidence.CurrentSchemaVersion,
            CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection,
            binding.WorkspaceId,
            binding.ExecutionBinding.RunId,
            binding.ExecutionBinding.Revision,
            binding.ExecutionBinding.ExecutionGeneration,
            activation.ActivationOrdinal,
            activation.VisitOrdinal,
            activation.NodeId,
            attempt,
            activation.CycleId,
            activation.CycleIteration,
            GovernedLoopControlCondition.Failure,
            selected,
            skipped,
            null,
            null,
            CustomLoopSequentialNodeDisposition.Rejected,
            CustomLoopSequentialOutcomeArtifactHash.Compute(runEvent),
            string.Empty));
        return new PublicationFailure(runEvent with { SequentialNodeEvidence = evidence }, selected, skipped);
    }

    private async Task<RunRead> ReadRunAsync(
        GovernedLoopExecutionBinding binding,
        CancellationToken cancellationToken)
    {
        CustomLoopRunRecord? run;
        try
        {
            run = await _runStore.GetAsync(binding.RunId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new RunRead(RunReadStatus.Unavailable, null);
        }

        if (run is null)
        {
            return new RunRead(RunReadStatus.NotFound, null);
        }

        return !CustomLoopRunValidator.Validate(run).IsValid
            || run.SequentialAdapterBinding is not { } adapterBinding
            || !Equals(adapterBinding.ExecutionBinding, binding)
            || run.Frontier is null
                ? new RunRead(RunReadStatus.Conflict, run)
                : new RunRead(RunReadStatus.Found, run);
    }

    private async Task<RunMutation> UpdateAsync(
        CustomLoopRunRecord current,
        CustomLoopRunRecord candidate,
        CancellationToken cancellationToken,
        Func<CustomLoopRunRecord, bool> isExactCommit)
    {
        if (!CustomLoopRunValidator.ValidateUpdate(current, candidate).IsValid)
        {
            return new RunMutation(RunMutationStatus.Conflict, current);
        }

        CustomLoopRunStoreResult? result;
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            result = await _runStore.UpdateAsync(candidate, current.LifecycleVersion, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Once the atomic write is attempted, cancellation and transport failures are outcome-ambiguous.
            // Reconcile from canonical truth below before deciding whether the exact phase committed.
            result = null;
        }

        if (result?.Status == CustomLoopRunStoreStatus.Updated
            && result.Run is { } updated
            && CustomLoopRunValidator.Validate(updated).IsValid
            && isExactCommit(updated))
        {
            return new RunMutation(RunMutationStatus.Committed, updated);
        }

        var read = await ReadRunAsync(current.SequentialAdapterBinding!.ExecutionBinding, CancellationToken.None).ConfigureAwait(false);
        if (read.Status == RunReadStatus.Found && isExactCommit(read.Run!))
        {
            return new RunMutation(RunMutationStatus.Replayed, read.Run);
        }

        return result?.Status switch
        {
            CustomLoopRunStoreStatus.Conflict or CustomLoopRunStoreStatus.OperationConflict or CustomLoopRunStoreStatus.TerminalImmutable
                => new RunMutation(RunMutationStatus.Conflict, read.Run),
            CustomLoopRunStoreStatus.NotFound => new RunMutation(RunMutationStatus.NotFound, null),
            _ when read.Status == RunReadStatus.Unavailable => new RunMutation(RunMutationStatus.Unavailable, null),
            _ => new RunMutation(RunMutationStatus.Ambiguous, read.Run),
        };
    }

    private static bool IsExactRunningWaitRequest(GovernedLoopSequentialNodeDispatchRequest? request)
        => request is not null
            && request.SchemaVersion == GovernedLoopSequentialNodeDispatchRequest.CurrentSchemaVersion
            && request.Anchor is not null
            && request.Plan is not null
            && request.Node is not null
            && request.Activation is
            {
                Status: GovernedLoopNodeExecutionStatus.Running,
                Attempt: not null,
                AttemptOperationId: not null,
            }
            && request.Attempt == request.Activation.Attempt
            && request.Node.Ordinal >= 0
            && request.Node.Ordinal < request.Plan.Nodes.Count
            && ReferenceEquals(request.Plan.Nodes[request.Node.Ordinal], request.Node)
            && request.Activation.PlanOrdinal == request.Node.Ordinal
            && string.Equals(request.Activation.NodeId, request.Node.NodeId, StringComparison.Ordinal)
            && GovernedLoopSequentialNodeDescriptors.IsWait(request.Node.Descriptor);

    private static bool IsRecoveryCandidate(CustomLoopRunRecord candidate)
    {
        try
        {
            return CustomLoopRunValidator.Validate(candidate).IsValid
                && candidate.SequentialAdapterBinding is not null
                && candidate.Frontier is not null;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NullReferenceException)
        {
            return false;
        }
    }

    private static bool HasRecoveryWork(CustomLoopRunRecord candidate)
        => candidate.WaitEvidence.Any(wait =>
            candidate.Frontier?.Payload.Nodes.ElementAtOrDefault(wait.ActivationOrdinal)?.Status switch
            {
                GovernedLoopNodeExecutionStatus.Waiting => wait.ParkEvidence is null,
                GovernedLoopNodeExecutionStatus.Running => wait.ParkEvidence is not null && wait.ContinuationEvidence is not null,
                _ => false,
            })
            || GovernedLoopWaitClaimEvidence.FindExactRecoverableClaims(candidate).Count > 0;

    private static bool HasPotentialWaitRecoveryWork(CustomLoopRunRecord candidate)
    {
        try
        {
            return candidate.WaitEvidence?.Count > 0
                || candidate.Frontier?.Payload.Nodes?.Any(node =>
                    node?.Descriptor.Kind == GovernedLoopNodeKind.Wait
                    && node.Status is GovernedLoopNodeExecutionStatus.Running or GovernedLoopNodeExecutionStatus.Waiting) == true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NullReferenceException)
        {
            return false;
        }
    }

    private static bool MatchesRequest(
        CustomLoopRunRecord run,
        GovernedLoopSequentialNodeDispatchRequest request)
        => run.SequentialAdapterBinding is { } binding
            && string.Equals(binding.ContentHash, request.Anchor.AdapterBinding.ContentHash, StringComparison.Ordinal)
            && Equals(binding.ExecutionBinding, request.Anchor.AdapterBinding.ExecutionBinding)
            && run.Frontier?.Payload.Nodes.ElementAtOrDefault(request.Activation.ActivationOrdinal) is { } activation
            && activation.PlanOrdinal == request.Activation.PlanOrdinal
            && activation.VisitOrdinal == request.Activation.VisitOrdinal
            && string.Equals(activation.NodeId, request.Activation.NodeId, StringComparison.Ordinal)
            && activation.Attempt == request.Attempt
            && string.Equals(activation.AttemptOperationId, request.Activation.AttemptOperationId, StringComparison.Ordinal)
            && activation.Status is GovernedLoopNodeExecutionStatus.Running or GovernedLoopNodeExecutionStatus.Waiting
            && GovernedLoopSequentialFrontierMachine.Validate(run.Frontier, binding, request.Plan);

    private static bool SamePlanBinding(CustomLoopRunRecord run, GovernedLoopWaitOrderedContext context)
        => run.SequentialAdapterBinding is { } binding
            && string.Equals(binding.ContentHash, context.Anchor.AdapterBinding.ContentHash, StringComparison.Ordinal)
            && Equals(binding.ExecutionBinding, context.Anchor.AdapterBinding.ExecutionBinding)
            && GovernedLoopSequentialFrontierMachine.Validate(run.Frontier, binding, context.Plan);

    private static bool TryCreateCondition(
        GovernedLoopSequentialNodeDispatchRequest request,
        out GovernedLoopWaitCondition condition)
    {
        var created = GovernedLoopWaitContractValidator.TryCreateCondition(
            request.Node.Descriptor,
            request.Node.Parameters,
            out var admitted,
            out var validation);
        condition = admitted!;
        return created && validation.IsValid;
    }

    private static bool IsValidContinuationRequest(
        GovernedLoopWakeContinuationRequest? request,
        bool reconcileOnly)
        => request?.Checkpoint is not null
            && request.Identity is not null
            && GovernedLoopSleepContractValidator.Validate(request.Checkpoint).IsValid
            && GovernedLoopSleepContractValidator.ValidateComposition(request.Checkpoint, request.Identity).IsValid
            && CustomLoopArtifactIdentifier.IsValid(request.ContinuationOperationId, CustomLoopLimits.MaxMutationOperationIdCharacters)
            && (reconcileOnly || request.PreparedWakeEvidence is not null && IsHash(request.ExpectedPostureHash))
            && (request.PreparedWakeEvidence is null
                || request.PreparedWakeEvidence.Disposition == GovernedLoopWakeDisposition.Prepared
                    && GovernedLoopSleepContractValidator.Validate(request.PreparedWakeEvidence).IsValid
                    && GovernedLoopSleepContractValidator.ValidateComposition(request.Checkpoint, request.PreparedWakeEvidence).IsValid
                    && string.Equals(request.PreparedWakeEvidence.Identity.ContentHash, request.Identity.ContentHash, StringComparison.Ordinal)
                    && string.Equals(request.PreparedWakeEvidence.ContinuationOperationId, request.ContinuationOperationId, StringComparison.Ordinal));

    private static bool MatchesCheckpoint(
        GovernedLoopWaitExecutionEvidence wait,
        GovernedLoopSleepCheckpoint checkpoint)
        => wait.ParkEvidence is { } park
            && string.Equals(park.Checkpoint.CheckpointId, checkpoint.CheckpointId, StringComparison.Ordinal)
            && string.Equals(park.Checkpoint.ContentHash, checkpoint.ContentHash, StringComparison.Ordinal);

    private static bool TryCreateParkEvidence(
        CustomLoopRunRecord run,
        GovernedLoopWaitExecutionEvidence wait,
        GovernedLoopSleepCheckpoint checkpoint,
        out GovernedLoopWaitParkEvidence parkEvidence)
    {
        parkEvidence = null!;
        if (run.SequentialAdapterBinding is not { } binding
            || !GovernedLoopSleepContractValidator.Validate(checkpoint).IsValid
            || !Equals(checkpoint.Binding.Execution, binding.ExecutionBinding)
            || !Equals(checkpoint.Binding.Publication, binding.AdmissionReceipt.Intent.Publication)
            || checkpoint.Binding.FrontierVersion != wait.ParkedFrontierVersion
            || !string.Equals(checkpoint.Binding.FrontierHash, wait.ParkedFrontierHash, StringComparison.Ordinal)
            || checkpoint.Binding.ActivationOrdinal != wait.ActivationOrdinal
            || !string.Equals(checkpoint.Binding.NodeId, wait.NodeId, StringComparison.Ordinal)
            || checkpoint.Binding.NodeVisitOrdinal != wait.NodeVisitOrdinal
            || !string.Equals(checkpoint.Binding.CycleId, wait.CycleId, StringComparison.Ordinal)
            || checkpoint.Binding.CycleIteration != wait.CycleIteration
            || checkpoint.Binding.WaitAttempt != wait.WaitAttempt
            || !string.Equals(checkpoint.Binding.WaitOperationId, wait.WaitOperationId, StringComparison.Ordinal)
            || checkpoint.PublishedAtUtc < wait.ParkedAtUtc
            || wait.Condition.WakeDeadlineUtc != checkpoint.WakeDeadlineUtc
            || !string.Equals(wait.Condition.AuthenticatedEventReference, checkpoint.AuthenticatedEventReference, StringComparison.Ordinal))
        {
            return false;
        }

        parkEvidence = GovernedLoopWaitContractHash.Apply(new GovernedLoopWaitParkEvidence(
            GovernedLoopWaitParkEvidence.CurrentSchemaVersion,
            wait.Condition,
            checkpoint,
            wait.ParkedAtUtc,
            string.Empty));
        return GovernedLoopWaitContractValidator.Validate(parkEvidence).IsValid;
    }

    private static bool MatchesPreparedWake(
        GovernedLoopWakeEvidence prepared,
        GovernedLoopWakeContinuationRequest request)
        => prepared.Disposition == GovernedLoopWakeDisposition.Prepared
            && string.Equals(prepared.Identity.WakeId, request.Identity.WakeId, StringComparison.Ordinal)
            && string.Equals(prepared.Identity.ContentHash, request.Identity.ContentHash, StringComparison.Ordinal)
            && string.Equals(prepared.ContinuationOperationId, request.ContinuationOperationId, StringComparison.Ordinal)
            && (request.PreparedWakeEvidence is null
                || string.Equals(prepared.ContentHash, request.PreparedWakeEvidence.ContentHash, StringComparison.Ordinal));

    private static bool IsParkedWith(
        CustomLoopRunRecord run,
        GovernedLoopWaitExecutionEvidence expected)
        => Wait(run, expected.ActivationOrdinal) is { } retained
            && string.Equals(retained.ContentHash, expected.ContentHash, StringComparison.Ordinal)
            && run.Frontier?.Payload.Nodes.ElementAtOrDefault(expected.ActivationOrdinal)?.Status == GovernedLoopNodeExecutionStatus.Waiting;

    private static bool HasExactContinuation(
        CustomLoopRunRecord run,
        int activationOrdinal,
        GovernedLoopWaitContinuationEvidence expected)
        => Wait(run, activationOrdinal)?.ContinuationEvidence is { } retained
            && string.Equals(retained.ContentHash, expected.ContentHash, StringComparison.Ordinal)
            && run.Frontier?.Payload.Nodes.ElementAtOrDefault(activationOrdinal)?.Status is GovernedLoopNodeExecutionStatus.Running or GovernedLoopNodeExecutionStatus.Completed;

    private static bool IsCompletedWith(
        CustomLoopRunRecord run,
        int activationOrdinal,
        string continuationHash)
    {
        var activation = run.Frontier?.Payload.Nodes.ElementAtOrDefault(activationOrdinal);
        var matching = run.Events.Where(item => item.SequentialNodeEvidence?.ActivationOrdinal == activationOrdinal
            && string.Equals(item.WaitContinuationEvidenceHash, continuationHash, StringComparison.Ordinal)).ToArray();
        return activation is
        {
            Status: GovernedLoopNodeExecutionStatus.Completed,
            OutcomeEvidenceId: not null,
            OutcomeEvidenceHash: not null,
        }
            && matching.Length == 1
            && string.Equals(activation.OutcomeEvidenceId, matching[0].EventId, StringComparison.Ordinal)
            && string.Equals(activation.OutcomeEvidenceHash, matching[0].SequentialNodeEvidence!.OutcomeArtifactHash, StringComparison.Ordinal);
    }

    private static GovernedLoopWaitExecutionEvidence? Wait(CustomLoopRunRecord? run, int activationOrdinal)
        => run?.WaitEvidence.SingleOrDefault(item => item.ActivationOrdinal == activationOrdinal);

    private static IReadOnlyList<GovernedLoopWaitExecutionEvidence> ReplaceWait(
        IReadOnlyList<GovernedLoopWaitExecutionEvidence> waits,
        GovernedLoopWaitExecutionEvidence replacement)
        => waits.Select(item => item.ActivationOrdinal == replacement.ActivationOrdinal ? replacement : item).ToArray();

    private static CustomLoopRunRecord Append(
        CustomLoopRunRecord run,
        DateTimeOffset updatedAtUtc,
        IReadOnlyList<CustomLoopRunEvent> events)
        => run with
        {
            LifecycleVersion = checked(run.LifecycleVersion + 1),
            UpdatedAtUtc = updatedAtUtc,
            Events = [.. run.Events, .. events],
        };

    private static CustomLoopRunEvent LifecycleEvent(
        CustomLoopRunRecord run,
        DateTimeOffset timestampUtc,
        string detail,
        string? eventId = null)
        => new(
            run.Events.Length + 1,
            eventId ?? $"wait-{Guid.NewGuid():N}",
            timestampUtc,
            CustomLoopRunEventKind.LifecycleChanged,
            null,
            null,
            null,
            detail,
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

    private static CustomLoopExecutionClock StopClock(CustomLoopExecutionClock clock, DateTimeOffset now)
    {
        var accumulated = clock.AccumulatedRunningMilliseconds;
        if (clock.ActiveSinceUtc is { } activeSince)
        {
            accumulated = checked(accumulated + Math.Max(0, (long)(now - activeSince).TotalMilliseconds));
        }

        return new CustomLoopExecutionClock(Math.Min(accumulated, CustomLoopLimits.MaxRunExecutionMilliseconds), null);
    }

    private bool TryReadUtcNow(DateTimeOffset floor, out DateTimeOffset now)
    {
        try
        {
            now = _timeProvider.GetUtcNow();
            if (floor != default && now < floor)
            {
                now = floor;
            }

            return IsUtc(now);
        }
        catch
        {
            now = default;
            return false;
        }
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right)
        => left >= right ? left : right;

    private static bool SameCondition(GovernedLoopWaitCondition left, GovernedLoopWaitCondition right)
        => string.Equals(left.ContentHash, right.ContentHash, StringComparison.Ordinal);

    private static bool IsHash(string? value)
        => value?.Length == GovernedLoopExecutionLimits.Sha256HexCharacters
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsUtc(DateTimeOffset value)
        => value != default && value.Offset == TimeSpan.Zero;

    private static GovernedLoopWaitParkResult Park(
        GovernedLoopWaitParkResultStatus status,
        GovernedLoopWaitParkEvidence? evidence = null,
        CustomLoopRunRecord? run = null,
        string? detail = null)
        => new(status, evidence, run, detail);

    private static GovernedLoopWakeContinuationResult Continuation(
        GovernedLoopWakeContinuationStatus status,
        string? evidenceHash = null,
        string? reference = null)
        => new(status, evidenceHash, reference);

    private static ContinuationPreparation Preparation(GovernedLoopWakeContinuationResult terminal)
        => new(null, null, terminal);

    private static GovernedLoopWaitParkResultStatus MapReadToPark(RunReadStatus status)
        => status switch
        {
            RunReadStatus.NotFound => GovernedLoopWaitParkResultStatus.NotFound,
            RunReadStatus.Unavailable => GovernedLoopWaitParkResultStatus.Unavailable,
            _ => GovernedLoopWaitParkResultStatus.Conflict,
        };

    private static GovernedLoopWakeContinuationStatus MapReadToContinuation(RunReadStatus status)
        => status switch
        {
            RunReadStatus.NotFound => GovernedLoopWakeContinuationStatus.NotCommitted,
            RunReadStatus.Unavailable => GovernedLoopWakeContinuationStatus.Unavailable,
            _ => GovernedLoopWakeContinuationStatus.Conflict,
        };

    private static GovernedLoopWaitParkResultStatus MapMutationToPark(RunMutationStatus status)
        => status switch
        {
            RunMutationStatus.NotFound => GovernedLoopWaitParkResultStatus.NotFound,
            RunMutationStatus.Unavailable => GovernedLoopWaitParkResultStatus.Unavailable,
            RunMutationStatus.Ambiguous => GovernedLoopWaitParkResultStatus.Ambiguous,
            _ => GovernedLoopWaitParkResultStatus.Conflict,
        };

    private static GovernedLoopWaitParkResultStatus MapPublicationToPark(GovernedLoopSleepPublicationStatus status)
        => status switch
        {
            GovernedLoopSleepPublicationStatus.NotFound => GovernedLoopWaitParkResultStatus.NotFound,
            GovernedLoopSleepPublicationStatus.Cancelled => GovernedLoopWaitParkResultStatus.Cancelled,
            GovernedLoopSleepPublicationStatus.Expired => GovernedLoopWaitParkResultStatus.Expired,
            GovernedLoopSleepPublicationStatus.Paused or GovernedLoopSleepPublicationStatus.ReviewBlocked or GovernedLoopSleepPublicationStatus.AmbiguousAttempt => GovernedLoopWaitParkResultStatus.ReviewBlocked,
            GovernedLoopSleepPublicationStatus.Unavailable => GovernedLoopWaitParkResultStatus.Unavailable,
            GovernedLoopSleepPublicationStatus.Ambiguous => GovernedLoopWaitParkResultStatus.Ambiguous,
            _ => GovernedLoopWaitParkResultStatus.Conflict,
        };

    private sealed record RunRead(RunReadStatus Status, CustomLoopRunRecord? Run);
    private sealed record RunMutation(RunMutationStatus Status, CustomLoopRunRecord? Run);
    private sealed record PublicationFailure(
        CustomLoopRunEvent Event,
        IReadOnlyList<string> SelectedControlEdgeIds,
        IReadOnlyList<string> SkippedControlEdgeIds);
    private sealed record ContinuationPreparation(
        CustomLoopRunRecord? Run,
        GovernedLoopWaitOrderedContext? Context,
        GovernedLoopWakeContinuationResult? Terminal);
}
