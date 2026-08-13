using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.Models;
using System.Text;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Loops.Wait;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

/// <summary>
/// Parks interrupted nonterminal runs at the last provable checkpoint without automatically dispatching work.
/// </summary>
/// <remarks>
/// Recovery distinguishes a checkpointed interruption from an unresolved canonical attempt, including a durable Running frontier
/// claim with no matching dispatch-start event. Open or incomplete admission evidence moves the run to review. An exact authenticated
/// canonical completion or definitive rejection closes its matching attempt, but recovery only parks that evidence at Paused for a
/// later explicit resume; it never advances a checkpoint or resumes execution automatically.
/// </remarks>
public sealed class CustomLoopRecoveryService
{
    private static readonly TimeSpan _integrityWriteTimeout = TimeSpan.FromSeconds(30);

    private readonly ICustomLoopRunStore _runStore;
    private readonly IAuditLog _auditLog;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="CustomLoopRecoveryService"/> type.
    /// </summary>
    /// <param name="runStore">The run store.</param>
    /// <param name="auditLog">The audit log.</param>
    /// <param name="timeProvider">The time provider.</param>
    public CustomLoopRecoveryService(ICustomLoopRunStore runStore, IAuditLog auditLog, TimeProvider? timeProvider = null)
    {
        _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Reconciles every nonterminal run after restart.
    /// </summary>
    /// <param name="actor">The authenticated actor requesting recovery. Each lifecycle transition remains attributed to the recovered run's retained admission actor.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>One unchanged, recovered, needs-review, or failed result per discovered run.</returns>
    public async Task<IReadOnlyList<CustomLoopRecoveryResult>> RecoverAsync(string actor, CancellationToken cancellationToken = default)
    {
        ValidateActor(actor);
        var runs = await _runStore.ListNonterminalAsync(cancellationToken);
        var results = new List<CustomLoopRecoveryResult>(runs.Count);
        foreach (var run in runs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await RecoverOneAsync(run, run.AdmissionActor, cancellationToken));
        }

        return results;
    }

    private async Task<CustomLoopRecoveryResult> RecoverOneAsync(CustomLoopRunRecord run, string actor, CancellationToken cancellationToken)
    {
        var validation = CustomLoopRunValidator.Validate(run);
        if (!validation.IsValid)
        {
            return Result(CustomLoopRecoveryStatus.Failed, run, "The persisted custom-loop run is invalid and recovery did not mutate it.");
        }

        var admissionAuditComplete = CustomLoopRunValidator.HasCompleteAdmissionAudit(run);
        var hasRestartSafeDeterministicAttempt = HasRestartSafeDeterministicAttemptSinceCheckpoint(run);
        var hasOpenAttempt = HasOpenAttemptSinceCheckpoint(run);
        var recoverableWaitClaims = GovernedLoopWaitClaimEvidence.FindExactRecoverableClaims(run);
        var recoverableWaitContinuations = GovernedLoopWaitClaimEvidence.FindExactRecoverableContinuations(run);
        if (run.Status == CustomLoopRunStatus.Paused && admissionAuditComplete && !hasOpenAttempt)
        {
            return Result(CustomLoopRecoveryStatus.Unchanged, run, "The run is already Paused; restart recovery never starts execution automatically.");
        }

        if (run.Status == CustomLoopRunStatus.Running
            && admissionAuditComplete
            && recoverableWaitClaims.Count + recoverableWaitContinuations.Count == 1
            && !hasOpenAttempt)
        {
            return Result(CustomLoopRecoveryStatus.Unchanged, run, "The run retains one exact restart-safe Wait recovery state for the canonical Wait recovery service; generic recovery did not replace its frontier or lifecycle truth.");
        }

        if (run.Status == CustomLoopRunStatus.Waiting
            && admissionAuditComplete
            && IsExactCanonicalWaitingCheckpoint(run)
            && !hasOpenAttempt)
        {
            return Result(CustomLoopRecoveryStatus.Unchanged, run, "The run retains exact canonical Waiting frontier and Wait evidence for the canonical Wait recovery service; generic recovery did not replace its checkpoint or lifecycle truth.");
        }

        // Open canonical-attempt evidence makes the outcome uncertain. This includes a durable
        // Running frontier claim that committed before its matching dispatch-start event. Such a
        // run requires review; recovery never guesses whether node work started or silently retries it.
        var target = !admissionAuditComplete
            ? CustomLoopRunStatus.NeedsReview
            : run.Status switch
            {
                CustomLoopRunStatus.Admitted => CustomLoopRunStatus.Paused,
                CustomLoopRunStatus.Paused when hasOpenAttempt => CustomLoopRunStatus.NeedsReview,
                CustomLoopRunStatus.Running or CustomLoopRunStatus.PauseRequested when hasOpenAttempt => CustomLoopRunStatus.NeedsReview,
                CustomLoopRunStatus.Running or CustomLoopRunStatus.PauseRequested => CustomLoopRunStatus.Paused,
                CustomLoopRunStatus.CancelRequested when hasRestartSafeDeterministicAttempt => CustomLoopRunStatus.NeedsReview,
                CustomLoopRunStatus.CancelRequested when hasOpenAttempt => CustomLoopRunStatus.NeedsReview,
                CustomLoopRunStatus.CancelRequested => CustomLoopRunStatus.Cancelled,
                _ => CustomLoopRunStatus.Unknown
            };

        if (target == CustomLoopRunStatus.Unknown)
        {
            return Result(CustomLoopRecoveryStatus.Failed, run, $"Recovery does not recognize nonterminal state {run.Status}; no mutation was attempted.");
        }

        var detail = (run.Status, target) switch
        {
            (_, CustomLoopRunStatus.NeedsReview) when !admissionAuditComplete => "Restart recovery found no valid durable admission-audit completion marker; execution is permanently stopped for review.",
            (CustomLoopRunStatus.CancelRequested, CustomLoopRunStatus.NeedsReview) when hasRestartSafeDeterministicAttempt => "Restart recovery found cancellation over an unadopted deterministic node attempt; operator reconciliation is required before any terminal disposition.",
            (CustomLoopRunStatus.Admitted, CustomLoopRunStatus.Paused) => "Restart recovery parked the admitted run at Paused without dispatch.",
            (_, CustomLoopRunStatus.NeedsReview) => "Restart recovery found an unresolved effectful or unauthenticated canonical attempt after the last committed checkpoint; execution remains stopped for review.",
            (_, CustomLoopRunStatus.Paused) when hasRestartSafeDeterministicAttempt => "Restart recovery parked an authenticated deterministic node attempt for explicit resume without evaluating it.",
            (CustomLoopRunStatus.CancelRequested, CustomLoopRunStatus.Cancelled) => "Restart recovery proved there was no open attempt after the checkpoint and completed cancellation without dispatch.",
            _ => "Restart recovery parked the interrupted run at its last proved checkpoint without dispatch."
        };
        var now = Now(run);
        var failureCode = !admissionAuditComplete
            ? "recovery_incomplete_admission_audit"
            : target == CustomLoopRunStatus.NeedsReview && run.Status == CustomLoopRunStatus.CancelRequested && hasRestartSafeDeterministicAttempt
                ? "recovery_deterministic_cancellation_reconciliation_required"
                : target == CustomLoopRunStatus.NeedsReview ? "recovery_open_attempt" : null;
        var candidate = CreateCandidate(run, target, failureCode, detail, now);
        var metadata = RecoveryMetadata(run, candidate, hasOpenAttempt, hasRestartSafeDeterministicAttempt, admissionAuditComplete);

        // Record intent before the lifecycle mutation so a crash never produces an unexplained
        // recovery transition.
        try
        {
            await _auditLog.AppendAsync(
                AuditEvent.Create(
                    actor,
                    AuditSchema.Actions.LoopRunLifecycle,
                    run.Id,
                    AuditSchema.Outcomes.Requested,
                    $"Restart recovery durably recorded its intent to transition {run.Status} to {target} before mutating the run.",
                    metadata),
                IntegrityToken());
        }
        catch (Exception exception)
        {
            return Result(CustomLoopRecoveryStatus.Failed, run, $"The recovery intent audit failed before lifecycle mutation: {SafeExceptionClass(exception)}.");
        }

        CustomLoopRunStoreResult stored;
        try
        {
            stored = await _runStore.UpdateAsync(candidate, run.LifecycleVersion, IntegrityToken());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (UnsupportedCustomLoopRunDiscoveryIndexSchemaException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Result(CustomLoopRecoveryStatus.Failed, run, $"The recovery transition failed: {SafeExceptionClass(exception)}.");
        }

        if (stored.Status is CustomLoopRunStoreStatus.Conflict or CustomLoopRunStoreStatus.TerminalImmutable)
        {
            var latest = await TryLoadAsync(run.Id);
            return Result(CustomLoopRecoveryStatus.Conflict, latest ?? run, "The run changed concurrently; restart recovery did not retry or start execution.");
        }

        if (stored.Status != CustomLoopRunStoreStatus.Updated || stored.Run is null)
        {
            return Result(CustomLoopRecoveryStatus.Failed, run, "The recovery transition was rejected; restart recovery did not start execution.");
        }

        var recovered = stored.Run;
        var outcome = target == CustomLoopRunStatus.NeedsReview ? AuditSchema.Outcomes.NeedsReview : AuditSchema.Outcomes.Succeeded;
        try
        {
            await _auditLog.AppendAsync(AuditEvent.Create(actor, AuditSchema.Actions.LoopRunLifecycle, recovered.Id, outcome, detail, metadata), IntegrityToken());
        }
        catch (Exception exception)
        {
            return Result(CustomLoopRecoveryStatus.Failed, recovered, $"The recovery transition is durable, but its lifecycle audit failed: {SafeExceptionClass(exception)}.");
        }

        var status = target switch
        {
            CustomLoopRunStatus.Paused => CustomLoopRecoveryStatus.Paused,
            CustomLoopRunStatus.Cancelled => CustomLoopRecoveryStatus.Cancelled,
            CustomLoopRunStatus.NeedsReview => CustomLoopRecoveryStatus.NeedsReview,
            _ => CustomLoopRecoveryStatus.Failed
        };
        return Result(status, recovered, detail);
    }

    private static bool IsExactCanonicalWaitingCheckpoint(CustomLoopRunRecord run)
    {
        if (run.SequentialAdapterBinding is null
            || run.Frontier?.Payload.Status != GovernedLoopFrontierStatus.Waiting)
        {
            return false;
        }

        var waiting = run.Frontier.Payload.Nodes
            .Where(node => node.Status == GovernedLoopNodeExecutionStatus.Waiting)
            .ToArray();
        return waiting.Length > 0
            && waiting.All(node => run.WaitEvidence.Count(wait =>
                wait.ActivationOrdinal == node.ActivationOrdinal
                && string.Equals(wait.NodeId, node.NodeId, StringComparison.Ordinal)
                && wait.NodeVisitOrdinal == node.VisitOrdinal
                && wait.WaitAttempt == node.Attempt
                && string.Equals(wait.WaitOperationId, node.AttemptOperationId, StringComparison.Ordinal)
                && wait.ContinuationEvidence is null) == 1
                && run.Events.Count(started =>
                    started.SequentialNodeEvidence?.ActivationOrdinal == node.ActivationOrdinal
                    && IsExactWaitingAttemptStart(run, started)) == 1);
    }

    private static Dictionary<string, object?> RecoveryMetadata(
        CustomLoopRunRecord current,
        CustomLoopRunRecord candidate,
        bool hasOpenAttempt,
        bool hasRestartSafeDeterministicAttempt,
        bool admissionAuditComplete)
    {
        return new Dictionary<string, object?>
        {
            ["runId"] = current.Id,
            ["loopId"] = current.LoopId,
            ["definitionVersion"] = current.AdmittedDefinition.DefinitionVersion,
            ["definitionHash"] = current.AdmittedDefinition.ContentHash,
            ["recovery"] = true,
            ["previousStatus"] = current.Status.ToString().ToLowerInvariant(),
            ["runStatus"] = candidate.Status.ToString().ToLowerInvariant(),
            ["previousLifecycleVersion"] = current.LifecycleVersion,
            ["lifecycleVersion"] = candidate.LifecycleVersion,
            ["recoveryEventId"] = candidate.Events[^1].EventId,
            ["openAttemptAfterCheckpoint"] = hasOpenAttempt,
            ["restartSafeDeterministicAttemptAfterCheckpoint"] = hasRestartSafeDeterministicAttempt,
            ["admissionAuditComplete"] = admissionAuditComplete,
            ["automaticExecution"] = false
        };
    }

    private static CustomLoopRunRecord CreateCandidate(
        CustomLoopRunRecord run,
        CustomLoopRunStatus status,
        string? failureCode,
        string detail,
        DateTimeOffset now)
    {
        var terminal = status is CustomLoopRunStatus.Cancelled or CustomLoopRunStatus.NeedsReview;
        var lifecycle = new CustomLoopRunEvent(run.Events.Length + 1, $"recovery-{Guid.NewGuid():N}", now, CustomLoopRunEventKind.LifecycleChanged, null, null, null, detail, [], null, null, null, null, null, null, null, null, null, null);
        return run with
        {
            LifecycleVersion = run.LifecycleVersion + 1,
            Status = status,
            UpdatedAtUtc = now,
            CompletedAtUtc = terminal ? now : null,
            ExecutionClock = StopAtLastDurableUpdate(run.ExecutionClock, run.UpdatedAtUtc),
            Events = [.. run.Events, lifecycle],
            FinalOutput = null,
            FailureCode = status == CustomLoopRunStatus.NeedsReview ? failureCode : null,
            FailureDetail = status == CustomLoopRunStatus.NeedsReview ? detail : null,
            Frontier = ProjectCanonicalFrontier(run, status, now),
        };
    }

    private static GovernedLoopFrontierPosture? ProjectCanonicalFrontier(
        CustomLoopRunRecord run,
        CustomLoopRunStatus status,
        DateTimeOffset now)
    {
        if (run.SequentialAdapterBinding is not { } binding
            || run.Frontier is not { } frontier
            || status == CustomLoopRunStatus.NeedsReview && frontier.Payload.Status == GovernedLoopFrontierStatus.ReviewBlocked)
        {
            return run.Frontier;
        }

        var running = frontier.Payload.Nodes.Where(candidate => candidate.Status == GovernedLoopNodeExecutionStatus.Running).ToArray();
        var exactOutcome = running.Length == 1 ? FindUniqueClosedSequentialOutcomeForProjection(run, running[0]) : null;
        var exactEvidence = exactOutcome?.SequentialNodeEvidence;
        var transition = status switch
        {
            CustomLoopRunStatus.NeedsReview when running.Length == 1
                => GovernedLoopSequentialFrontierMachine.ReviewBlockCurrent(
                    frontier,
                    binding,
                    exactOutcome?.EventId,
                    exactEvidence?.OutcomeArtifactHash,
                    exactEvidence?.ControlOutcome,
                    exactEvidence?.SelectedControlEdgeIds,
                    exactEvidence?.SkippedControlEdgeIds,
                    now),
            CustomLoopRunStatus.NeedsReview when running.Length == 0
                => GovernedLoopSequentialFrontierMachine.ReviewBlockAggregate(frontier, binding, now),
            CustomLoopRunStatus.Cancelled
                => GovernedLoopSequentialFrontierMachine.CancelCurrent(frontier, binding, now),
            _ => null,
        };
        if (transition is null)
        {
            return run.Frontier;
        }

        return transition.Status == GovernedLoopSequentialFrontierTransitionStatus.Applied
            && transition.Frontier is not null
                ? transition.Frontier
                : throw new InvalidOperationException(
                    "Restart recovery could not atomically project the canonical frontier to its lifecycle posture.");
    }

    private static bool HasOpenAttemptSinceCheckpoint(CustomLoopRunRecord run)
    {
        var hasUnresolvedDispatch = run.Events.Any(item => item.Sequence > run.Checkpoint.LastCommittedSequence
            && (item.Kind is CustomLoopRunEventKind.NodeAttemptStarted or CustomLoopRunEventKind.ExitDecisionStarted)
            && !HasAuthenticatedTerminalSequentialOutcome(run, item)
            && !IsRestartSafeDeterministicAttemptStart(run, item)
            && !GovernedLoopWaitClaimEvidence.IsExactRecoverableClaimStart(run, item)
            && !GovernedLoopWaitClaimEvidence.IsExactRecoverableContinuationStart(run, item)
            && !IsExactWaitingAttemptStart(run, item));
        return hasUnresolvedDispatch || HasUnresolvedRunningFrontierClaim(run);
    }

    private static bool HasUnresolvedRunningFrontierClaim(CustomLoopRunRecord run)
    {
        var runningNodes = run.Frontier?.Payload.Nodes
            .Where(node => node.Status == GovernedLoopNodeExecutionStatus.Running)
            .Take(2)
            .ToArray() ?? [];
        if (runningNodes.Length != 1)
        {
            return false;
        }

        var running = runningNodes[0];
        var exactStarts = run.Events.Where(item => item.Sequence > run.Checkpoint.LastCommittedSequence
            && (item.Kind is CustomLoopRunEventKind.NodeAttemptStarted or CustomLoopRunEventKind.ExitDecisionStarted)
            && string.Equals(item.EventId, running.AttemptOperationId, StringComparison.Ordinal)
            && item.SequentialNodeEvidence is { } dispatch
            && string.Equals(dispatch.NodeId, running.NodeId, StringComparison.Ordinal)
            && dispatch.Attempt == running.Attempt
            && StartedAttemptMatchesFrontier(run, item, dispatch))
            .Take(2)
            .ToArray();
        return exactStarts.Length != 1
            || (!HasAuthenticatedTerminalSequentialOutcome(run, exactStarts[0])
                && !IsRestartSafeDeterministicAttemptStart(run, exactStarts[0])
                && !GovernedLoopWaitClaimEvidence.IsExactRecoverableClaimStart(run, exactStarts[0])
                && !GovernedLoopWaitClaimEvidence.IsExactRecoverableContinuationStart(run, exactStarts[0])
                && !IsExactWaitingAttemptStart(run, exactStarts[0]));
    }

    private static bool IsExactWaitingAttemptStart(CustomLoopRunRecord run, CustomLoopRunEvent started)
    {
        if (run.Status != CustomLoopRunStatus.Waiting
            || run.Frontier?.Payload.Status != GovernedLoopFrontierStatus.Waiting
            || started.Kind != CustomLoopRunEventKind.NodeAttemptStarted
            || started.SequentialNodeEvidence is not
            {
                Kind: CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
                Disposition: CustomLoopSequentialNodeDisposition.Unknown,
            } dispatch
            || run.SequentialAdapterBinding is not { } binding
            || !SequentialBindingMatchesRun(dispatch, run, binding)
            || !CustomLoopSequentialNodeEvidenceHash.Matches(dispatch)
            || !CustomLoopSequentialOutcomeArtifactHash.Matches(started)
            || !StartedAttemptMatchesFrontier(run, started, dispatch))
        {
            return false;
        }

        var activation = run.Frontier.Payload.Nodes.SingleOrDefault(node =>
            node.ActivationOrdinal == dispatch.ActivationOrdinal);
        return activation?.Status == GovernedLoopNodeExecutionStatus.Waiting
            && run.WaitEvidence.Count(wait =>
                wait.ActivationOrdinal == dispatch.ActivationOrdinal
                && string.Equals(wait.NodeId, dispatch.NodeId, StringComparison.Ordinal)
                && wait.NodeVisitOrdinal == dispatch.VisitOrdinal
                && wait.WaitAttempt == dispatch.Attempt
                && string.Equals(wait.WaitOperationId, started.EventId, StringComparison.Ordinal)
                && wait.ContinuationEvidence is null) == 1;
    }

    private static bool HasAuthenticatedTerminalSequentialOutcome(CustomLoopRunRecord run, CustomLoopRunEvent started)
    {
        if (started.SequentialNodeEvidence is not
            {
                Kind: CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
                Disposition: CustomLoopSequentialNodeDisposition.Unknown,
            } dispatch
            || started.Kind is not (CustomLoopRunEventKind.NodeAttemptStarted or CustomLoopRunEventKind.ExitDecisionStarted)
            || started.Iteration is not > 0
            || started.Attempt != dispatch.Attempt
            || run.SequentialAdapterBinding is not { } binding
            || !SequentialBindingMatchesRun(dispatch, run, binding)
            || !CustomLoopSequentialNodeEvidenceHash.Matches(dispatch)
            || !CustomLoopSequentialOutcomeArtifactHash.Matches(started)
            || !StartedAttemptMatchesFrontier(run, started, dispatch))
        {
            return false;
        }

        var terminals = run.Events.Where(item => item.Sequence > started.Sequence
            && item.SequentialNodeEvidence is { } outcome
            && item.Iteration == started.Iteration
            && string.Equals(item.StepId, started.StepId, StringComparison.Ordinal)
            && item.Attempt == started.Attempt
            && SameSequentialBinding(outcome, dispatch)
            && SameSequentialCoordinates(outcome, dispatch)
            && IsResolvedSequentialOutcome(item.Kind, outcome)
            && CustomLoopSequentialNodeEvidenceHash.Matches(outcome)
            && CustomLoopSequentialOutcomeArtifactHash.Matches(item))
            .Take(2)
            .ToArray();
        return terminals.Length == 1;
    }

    private static bool StartedAttemptMatchesFrontier(
        CustomLoopRunRecord run,
        CustomLoopRunEvent started,
        CustomLoopSequentialNodeEvidence dispatch)
    {
        var matchingNodes = run.Frontier?.Payload.Nodes.Where(node => node is
        {
            Attempt: { } attempt,
            AttemptOperationId: { } attemptOperationId,
        }
            && node.ActivationOrdinal == dispatch.ActivationOrdinal
            && node.VisitOrdinal == dispatch.VisitOrdinal
            && attempt == dispatch.Attempt
            && started.Attempt == attempt
            && string.Equals(attemptOperationId, started.EventId, StringComparison.Ordinal)
            && string.Equals(node.NodeId, dispatch.NodeId, StringComparison.Ordinal)
            && string.Equals(node.CycleId, dispatch.CycleId, StringComparison.Ordinal)
            && node.CycleIteration == dispatch.CycleIteration
            && string.Equals(
                started.StepId,
                node.Descriptor.Kind == GovernedLoopNodeKind.Exit ? "exit" : node.NodeId,
                StringComparison.Ordinal))
            .Take(2)
            .ToArray() ?? [];
        return matchingNodes.Length == 1;
    }

    private static bool SequentialBindingMatchesRun(
        CustomLoopSequentialNodeEvidence evidence,
        CustomLoopRunRecord run,
        GovernedLoopSequentialAdapterBinding binding)
        => string.Equals(evidence.WorkspaceId, binding.WorkspaceId, StringComparison.Ordinal)
            && string.Equals(evidence.RunId, run.Id, StringComparison.Ordinal)
            && Equals(evidence.Revision, binding.ExecutionBinding.Revision)
            && evidence.ExecutionGeneration == binding.ExecutionBinding.ExecutionGeneration;

    private static bool SameSequentialBinding(
        CustomLoopSequentialNodeEvidence candidate,
        CustomLoopSequentialNodeEvidence expected)
        => string.Equals(candidate.WorkspaceId, expected.WorkspaceId, StringComparison.Ordinal)
            && string.Equals(candidate.RunId, expected.RunId, StringComparison.Ordinal)
            && Equals(candidate.Revision, expected.Revision)
            && candidate.ExecutionGeneration == expected.ExecutionGeneration;

    private static bool SameSequentialCoordinates(
        CustomLoopSequentialNodeEvidence candidate,
        CustomLoopSequentialNodeEvidence expected)
        => candidate.ActivationOrdinal == expected.ActivationOrdinal
            && candidate.VisitOrdinal == expected.VisitOrdinal
            && string.Equals(candidate.NodeId, expected.NodeId, StringComparison.Ordinal)
            && candidate.Attempt == expected.Attempt
            && string.Equals(candidate.CycleId, expected.CycleId, StringComparison.Ordinal)
            && candidate.CycleIteration == expected.CycleIteration;

    private static bool IsResolvedSequentialOutcome(
        CustomLoopRunEventKind eventKind,
        CustomLoopSequentialNodeEvidence evidence)
        => (eventKind, evidence.Kind, evidence.Disposition) is
            (CustomLoopRunEventKind.NodeAttemptCompleted or CustomLoopRunEventKind.NodeOutcomeObserved or CustomLoopRunEventKind.ExitDecisionCompleted,
                CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
                CustomLoopSequentialNodeDisposition.Completed)
            or (CustomLoopRunEventKind.NodeAttemptFailed,
                CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection,
                CustomLoopSequentialNodeDisposition.Rejected);

    internal static bool HasRestartSafeDeterministicAttemptSinceCheckpoint(CustomLoopRunRecord run)
        => run.Events.Any(item => item.Sequence > run.Checkpoint.LastCommittedSequence && IsRestartSafeDeterministicAttemptStart(run, item));

    private static bool IsRestartSafeDeterministicAttemptStart(CustomLoopRunRecord run, CustomLoopRunEvent item)
    {
        if (run.Frontier is not { } frontier
            || run.SequentialAdapterBinding is not { } binding
            || item is not
            {
                Kind: CustomLoopRunEventKind.NodeAttemptStarted,
                Iteration: > 0,
                TraceReservationUtf8Bytes: CustomLoopLimits.MaxGraphPureNodeOutcomeEvidenceReservationUtf8Bytes,
                SequentialNodeEvidence:
                {
                    Kind: CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
                    Disposition: CustomLoopSequentialNodeDisposition.Unknown,
                } evidence,
            }
            || frontier.Payload.Nodes.SingleOrDefault(candidate =>
                candidate.ActivationOrdinal == evidence.ActivationOrdinal
                && candidate.VisitOrdinal == evidence.VisitOrdinal
                && string.Equals(candidate.NodeId, evidence.NodeId, StringComparison.Ordinal)
                && candidate.Status == GovernedLoopNodeExecutionStatus.Running) is not
                {
                    Attempt: { } attempt,
                    AttemptOperationId: { } attemptOperationId,
                } node)
        {
            return false;
        }

        var exactStart = string.Equals(item.EventId, attemptOperationId, StringComparison.Ordinal)
            && item.Attempt == attempt
            && string.Equals(item.StepId, node.NodeId, StringComparison.Ordinal)
            && evidence.ActivationOrdinal == node.ActivationOrdinal
            && evidence.VisitOrdinal == node.VisitOrdinal
            && evidence.Attempt == attempt
            && string.Equals(evidence.NodeId, node.NodeId, StringComparison.Ordinal)
            && string.Equals(evidence.CycleId, node.CycleId, StringComparison.Ordinal)
            && evidence.CycleIteration == node.CycleIteration
            && SequentialBindingMatchesRun(evidence, run, binding)
            && CustomLoopSequentialNodeEvidenceHash.Matches(evidence)
            && CustomLoopSequentialOutcomeArtifactHash.Matches(item);
        return exactStart
            && (node.Descriptor.Kind is GovernedLoopNodeKind.Transform or GovernedLoopNodeKind.Validate
                || node.Descriptor.Kind is GovernedLoopNodeKind.Condition or GovernedLoopNodeKind.Join
                    && HasExactClosedTopologyOutcome(run, binding, node, attempt, item.Sequence));
    }

    private static bool HasExactClosedTopologyOutcome(
        CustomLoopRunRecord run,
        GovernedLoopSequentialAdapterBinding binding,
        GovernedLoopNodeExecutionEvidence node,
        int attempt,
        long startSequence)
    {
        var outcomes = run.Events.Where(item => item.Sequence > startSequence
            && item.Kind == CustomLoopRunEventKind.NodeAttemptCompleted
            && item.Attempt == attempt
            && string.Equals(item.StepId, node.NodeId, StringComparison.Ordinal)
            && item.Iteration == (node.CycleIteration ?? 1)
            && item.SequentialNodeEvidence is
            {
                Kind: CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
                Disposition: CustomLoopSequentialNodeDisposition.Completed,
                ControlOutcome: { } controlOutcome,
            } evidence
            && (node.Descriptor.Kind == GovernedLoopNodeKind.Condition
                ? controlOutcome is GovernedLoopControlCondition.True or GovernedLoopControlCondition.False
                : controlOutcome == GovernedLoopControlCondition.Success)
            && evidence.ActivationOrdinal == node.ActivationOrdinal
            && evidence.VisitOrdinal == node.VisitOrdinal
            && evidence.Attempt == attempt
            && string.Equals(evidence.NodeId, node.NodeId, StringComparison.Ordinal)
            && string.Equals(evidence.CycleId, node.CycleId, StringComparison.Ordinal)
            && evidence.CycleIteration == node.CycleIteration
            && SequentialBindingMatchesRun(evidence, run, binding)
            && CustomLoopSequentialNodeEvidenceHash.Matches(evidence)
            && CustomLoopSequentialOutcomeArtifactHash.Matches(item))
            .Take(2)
            .ToArray();
        return outcomes.Length == 1;
    }

    private static CustomLoopRunEvent? FindUniqueClosedSequentialOutcomeForProjection(
        CustomLoopRunRecord run,
        GovernedLoopNodeExecutionEvidence activation)
    {
        if (run.SequentialAdapterBinding is not { } binding || activation.Attempt is not { } attempt)
        {
            return null;
        }

        var dispatchStarts = run.Events.Where(item => item.SequentialNodeEvidence is
        {
            Kind: CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
            Disposition: CustomLoopSequentialNodeDisposition.Unknown,
        } evidence
            && item.Kind is CustomLoopRunEventKind.NodeAttemptStarted or CustomLoopRunEventKind.ExitDecisionStarted
            && item.Iteration is > 0
            && item.Attempt == attempt
            && item.Sequence > run.Checkpoint.LastCommittedSequence
            && string.Equals(
                item.StepId,
                activation.Descriptor.Kind == GovernedLoopNodeKind.Exit ? "exit" : activation.NodeId,
                StringComparison.Ordinal)
            && evidence.ActivationOrdinal == activation.ActivationOrdinal
            && evidence.VisitOrdinal == activation.VisitOrdinal
            && string.Equals(evidence.NodeId, activation.NodeId, StringComparison.Ordinal)
            && evidence.Attempt == attempt
            && string.Equals(evidence.CycleId, activation.CycleId, StringComparison.Ordinal)
            && evidence.CycleIteration == activation.CycleIteration
            && SequentialBindingMatchesRun(evidence, run, binding)
            && CustomLoopSequentialNodeEvidenceHash.Matches(evidence)
            && CustomLoopSequentialOutcomeArtifactHash.Matches(item))
            .Take(2)
            .ToArray();
        if (dispatchStarts.Length != 1)
        {
            return null;
        }

        var dispatchStart = dispatchStarts[0];
        var matches = run.Events.Where(item => item.SequentialNodeEvidence is { } evidence
            && item.Sequence > dispatchStart.Sequence
            && item.Iteration is > 0
            && (activation.CycleIteration is null || item.Iteration == activation.CycleIteration)
            && item.Attempt == attempt
            && string.Equals(
                item.StepId,
                activation.Descriptor.Kind == GovernedLoopNodeKind.Exit ? "exit" : activation.NodeId,
                StringComparison.Ordinal)
            && IsClosedSequentialOutcome(item.Kind, evidence)
            && evidence.ActivationOrdinal == activation.ActivationOrdinal
            && evidence.VisitOrdinal == activation.VisitOrdinal
            && string.Equals(evidence.NodeId, activation.NodeId, StringComparison.Ordinal)
            && evidence.Attempt == attempt
            && string.Equals(evidence.CycleId, activation.CycleId, StringComparison.Ordinal)
            && evidence.CycleIteration == activation.CycleIteration
            && string.Equals(evidence.WorkspaceId, binding.WorkspaceId, StringComparison.Ordinal)
            && string.Equals(evidence.RunId, binding.ExecutionBinding.RunId, StringComparison.Ordinal)
            && Equals(evidence.Revision, binding.ExecutionBinding.Revision)
            && evidence.ExecutionGeneration == binding.ExecutionBinding.ExecutionGeneration
            && CustomLoopSequentialNodeEvidenceHash.Matches(evidence)
            && CustomLoopSequentialOutcomeArtifactHash.Matches(item))
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool IsClosedSequentialOutcome(
        CustomLoopRunEventKind eventKind,
        CustomLoopSequentialNodeEvidence evidence)
        => IsResolvedSequentialOutcome(eventKind, evidence)
            || eventKind == CustomLoopRunEventKind.NodeAttemptFailed
                && evidence is
                {
                    Kind: CustomLoopSequentialNodeEvidenceKind.AmbiguityAttention,
                    Disposition: CustomLoopSequentialNodeDisposition.NeedsReview,
                };

    private static CustomLoopExecutionClock StopAtLastDurableUpdate(CustomLoopExecutionClock clock, DateTimeOffset durableStop)
    {
        var accumulated = clock.AccumulatedRunningMilliseconds;
        if (clock.ActiveSinceUtc is { } activeSince)
        {
            accumulated = checked(accumulated + Math.Max(0, (long)(durableStop - activeSince).TotalMilliseconds));
        }

        return new CustomLoopExecutionClock(Math.Min(accumulated, CustomLoopLimits.MaxRunExecutionMilliseconds), null);
    }

    private async Task<CustomLoopRunRecord?> TryLoadAsync(string runId)
    {
        try
        {
            return await _runStore.GetAsync(runId, IntegrityToken());
        }
        catch
        {
            return null;
        }
    }

    private DateTimeOffset Now(CustomLoopRunRecord run)
    {
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        return now < run.UpdatedAtUtc ? run.UpdatedAtUtc : now;
    }

    private static void ValidateActor(string actor)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > CustomLoopLimits.MaxTraceReferenceCharacters || actor.Any(character => char.IsControl(character) || char.IsSurrogate(character)) || !actor.IsNormalized(NormalizationForm.FormC))
        {
            throw new ArgumentException("Actor must be bounded normalized text without control or invalid surrogate characters.", nameof(actor));
        }
    }

    private static CancellationToken IntegrityToken()
    {
        return new CancellationTokenSource(_integrityWriteTimeout).Token;
    }

    private static string SafeExceptionClass(Exception exception)
    {
        return exception.GetType().Name;
    }

    private static CustomLoopRecoveryResult Result(CustomLoopRecoveryStatus status, CustomLoopRunRecord run, string detail)
    {
        return new CustomLoopRecoveryResult(status, run, detail);
    }
}
