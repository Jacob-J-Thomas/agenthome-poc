using EmbodySense.Core.Application.HumanInput.Continuations.Models;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Application.HumanInput.Responses;
using EmbodySense.Core.Application.HumanInput.Responses.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.Failures;
using EmbodySense.Core.Application.Loops.Failures.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Application.Loops.Wait;
using EmbodySense.Core.Application.Loops.Wait.Models;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses;
using EmbodySense.Core.Common.HumanInput.Responses.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Common.Loops.Failures.Models;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints.Models;
using EmbodySense.Core.Common.Loops.HumanInput;
using EmbodySense.Core.Common.Loops.HumanInput.Policies.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;

namespace EmbodySense.Core.Application.HumanInput.Continuations;

/// <summary>Bridges accepted Human Input selections to the canonical generic sleep/wake continuation plane.</summary>
/// <remarks>
/// This service owns no worker lifetime, timer, queue, lease, or wake ledger. The canonical run retains the selected
/// response and terminal receipt; the generic sleep store retains wake identity, prepared/ambiguous/committed evidence,
/// and coordinator ownership. A selection is durably attached to its exact checkpoint before wake submission, and the
/// terminal checkpoint plus completed activation frontier are durably committed before ordered re-entry.
/// </remarks>
public sealed class HumanInputResponseContinuationService : IGovernedLoopAuthenticatedWakeVerificationPort, IGovernedLoopWakeContinuationPort
{
    private const string TerminalReceiptPrefix = "human-input-";
    private readonly ICustomLoopRunStore _runs;
    private readonly IHumanInputResponseLifecycleStore _responses;
    private readonly IGovernedLoopSleepStore _sleepStore;
    private readonly IGovernedLoopSleepCurrentPosturePort _currentPosture;
    private readonly IGovernedLoopWaitOrderedResumePort _contexts;
    private readonly IGovernedLoopSequentialOrderedRuntime _orderedRuntime;
    private readonly TimeProvider _timeProvider;
    private GovernedLoopSleepService? _sleep;

    /// <summary>Creates a host-neutral Human Input response-continuation bridge over canonical run, response, posture, and ordered-runtime ports.</summary>
    public HumanInputResponseContinuationService(
        ICustomLoopRunStore runs,
        IHumanInputResponseLifecycleStore responses,
        IGovernedLoopSleepStore sleepStore,
        IGovernedLoopSleepCurrentPosturePort currentPosture,
        IGovernedLoopWaitOrderedResumePort contexts,
        IGovernedLoopSequentialOrderedRuntime orderedRuntime,
        TimeProvider? timeProvider = null)
    {
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _responses = responses ?? throw new ArgumentNullException(nameof(responses));
        _sleepStore = sleepStore ?? throw new ArgumentNullException(nameof(sleepStore));
        _currentPosture = currentPosture ?? throw new ArgumentNullException(nameof(currentPosture));
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _orderedRuntime = orderedRuntime ?? throw new ArgumentNullException(nameof(orderedRuntime));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Binds the one already-composed generic sleep service before response discovery begins.</summary>
    /// <remarks>The binding breaks the construction cycle only; all wake ownership remains in the generic service.</remarks>
    public void BindSleep(GovernedLoopSleepService sleep)
    {
        ArgumentNullException.ThrowIfNull(sleep);
        if (Interlocked.CompareExchange(ref _sleep, sleep, null) is not null)
        {
            throw new InvalidOperationException("The Human Input response continuation service may bind one generic sleep service exactly once.");
        }
    }

    /// <summary>Discovers one selected response, durably attaches it to its checkpoint, and submits the generic authenticated wake.</summary>
    /// <param name="candidate">The run/checkpoint candidate reread from canonical discovery state.</param>
    /// <param name="cancellationToken">Cancels before durable selection attachment or generic wake preparation.</param>
    /// <returns>A closed result; duplicate calls reuse canonical evidence and do not redispatch a completed continuation.</returns>
    public async Task<HumanInputResponseContinuationWakeResult> WakeAsync(
        HumanInputResponseContinuationCandidate? candidate,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCandidate(candidate) || Volatile.Read(ref _sleep) is not { } sleep)
        {
            return Wake(HumanInputResponseContinuationWakeStatus.Invalid);
        }

        var initialRead = await ReadRunAsync(candidate!.RunId, cancellationToken).ConfigureAwait(false);
        if (initialRead.Status != HumanInputResponseContinuationRunReadStatus.Found)
        {
            return Wake(Map(initialRead.Status));
        }
        var initial = initialRead.Run!;
        if (TryFindAcceptedTerminalReplay(initial, candidate.CheckpointId, out var terminalCheckpoint, out var terminalActivation))
        {
            return await ReplayTerminalWakeAsync(initial, terminalCheckpoint!, terminalActivation!, sleep, cancellationToken).ConfigureAwait(false);
        }
        if (TryFindNoResponseReentry(initial, candidate.CheckpointId, out var retiredCheckpoint))
        {
            return await ResumeNoResponseIfRequiredAsync(initial, retiredCheckpoint!, cancellationToken).ConfigureAwait(false)
                ? Wake(HumanInputResponseContinuationWakeStatus.Retired)
                : Wake(HumanInputResponseContinuationWakeStatus.Unavailable);
        }
        if (!TryFindWaitingCheckpoint(initial, candidate.CheckpointId, out var checkpoint, out var activation))
        {
            return Wake(HumanInputResponseContinuationWakeStatus.Stale);
        }

        var attached = await AttachSelectionAsync(candidate, cancellationToken).ConfigureAwait(false);
        if (attached.Status is not (SelectionAttachmentStatus.Attached or SelectionAttachmentStatus.Replayed)
            || attached.Selection is null)
        {
            return Wake(Map(attached.Status));
        }

        var selectedRead = await ReadRunAsync(candidate.RunId, cancellationToken).ConfigureAwait(false);
        if (selectedRead.Status != HumanInputResponseContinuationRunReadStatus.Found)
        {
            return Wake(Map(selectedRead.Status));
        }

        var selectedRun = selectedRead.Run!;
        if (!TryFindWaitingCheckpoint(selectedRun, candidate.CheckpointId, out var selectedCheckpoint, out var selectedActivation)
            || !TryCreatePublication(selectedRun, selectedCheckpoint!, selectedActivation!, out var publication))
        {
            return Wake(HumanInputResponseContinuationWakeStatus.Stale);
        }

        GovernedLoopSleepPublicationResult published;
        try
        {
            published = await sleep.PublishAsync(publication, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Wake(HumanInputResponseContinuationWakeStatus.Unavailable);
        }

        if (published.Status is not (GovernedLoopSleepPublicationStatus.Published or GovernedLoopSleepPublicationStatus.Replayed)
            || published.Checkpoint is null)
        {
            return Wake(Map(published.Status));
        }

        GovernedLoopWakeResult result;
        try
        {
            result = await sleep.WakeAsync(
                new GovernedLoopWakeRequest(
                    published.Checkpoint.CheckpointId,
                    published.Checkpoint.ContentHash,
                    attached.Selection.SelectionHash),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Wake(HumanInputResponseContinuationWakeStatus.Unavailable);
        }

        return result.Status switch
        {
            GovernedLoopWakeResultStatus.Committed => Wake(HumanInputResponseContinuationWakeStatus.Submitted, result),
            GovernedLoopWakeResultStatus.Duplicate => Wake(HumanInputResponseContinuationWakeStatus.Replayed, result),
            GovernedLoopWakeResultStatus.Invalid or GovernedLoopWakeResultStatus.Conflict or GovernedLoopWakeResultStatus.Failed => Wake(HumanInputResponseContinuationWakeStatus.Invalid, result),
            GovernedLoopWakeResultStatus.Unavailable or GovernedLoopWakeResultStatus.AmbiguousAttempt => Wake(HumanInputResponseContinuationWakeStatus.Unavailable, result),
            _ => Wake(HumanInputResponseContinuationWakeStatus.Stale, result),
        };
    }

    /// <inheritdoc />
    public async Task<GovernedLoopAuthenticatedWakeVerificationResult?> VerifyAsync(
        GovernedLoopAuthenticatedWakeVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null
            || !IsHash(request.AuthenticationEvidenceHash)
            || !IsHash(request.CheckpointId)
            || !IsHash(request.CheckpointHash)
            || !TryNow(out var authenticatedAtUtc))
        {
            return Verification(GovernedLoopAuthenticatedWakeVerificationStatus.Rejected);
        }

        var resolved = await ReadRunBySleepCheckpointAsync(request, cancellationToken).ConfigureAwait(false);
        if (resolved.Status == HumanInputResponseContinuationWakeResolutionStatus.NotFound)
        {
            return Verification(GovernedLoopAuthenticatedWakeVerificationStatus.NotFound);
        }
        if (resolved.Status == HumanInputResponseContinuationWakeResolutionStatus.Unavailable)
        {
            return Verification(GovernedLoopAuthenticatedWakeVerificationStatus.Unavailable);
        }
        if (resolved.Status != HumanInputResponseContinuationWakeResolutionStatus.Found
            || resolved.Run is null
            || resolved.Checkpoint is null
            || !TryFindCheckpointForWake(resolved.Run, resolved.Checkpoint, out var checkpoint, out _)
            || checkpoint!.Posture != GovernedLoopHumanInputWaitingCheckpointPosture.AnsweredNotResumed
            || !TrySelectionReference(checkpoint, out var reference)
            || !string.Equals(reference!.SelectionHash, request.AuthenticationEvidenceHash, StringComparison.Ordinal))
        {
            return Verification(GovernedLoopAuthenticatedWakeVerificationStatus.Rejected);
        }

        var response = await ReadResponseAsync(checkpoint.Request, cancellationToken).ConfigureAwait(false);
        if (response is null || response.Status != HumanInputResponseLifecycleStoreReadStatus.Ready)
        {
            return Verification(GovernedLoopAuthenticatedWakeVerificationStatus.Unavailable);
        }

        var expectedRequest = new HumanInputRequestReference(
            HumanInputRequestReference.CurrentSchemaVersion,
            checkpoint.Request.RequestId,
            checkpoint.Request.RequestVersionId,
            checkpoint.Request.RequestHash);
        if (response.Snapshot is null
            || !HumanInputResponseLifecycleStoreSnapshotGuard.TryCapture(response.Snapshot, expectedRequest, out var snapshot)
            || snapshot?.Selection is not { } selection
            || !HumanInputResponseSelectionHash.Matches(selection))
        {
            return Verification(GovernedLoopAuthenticatedWakeVerificationStatus.Rejected);
        }

        if (!SelectionMatches(checkpoint, reference, selection)
            || selection.SelectedAtUtc < request.CheckpointPublishedAtUtc
            || selection.SelectedAtUtc > authenticatedAtUtc)
        {
            return Verification(GovernedLoopAuthenticatedWakeVerificationStatus.Rejected);
        }

        return new GovernedLoopAuthenticatedWakeVerificationResult(
            GovernedLoopAuthenticatedWakeVerificationStatus.Verified,
            new GovernedLoopAuthenticatedWakeVerification(
                request.CheckpointId,
                request.CheckpointHash,
                request.AuthenticatedEventReference,
                request.AuthenticationEvidenceHash,
                selection.SelectedAtUtc,
                authenticatedAtUtc,
                Eligible: true));
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
        GovernedLoopWakeContinuationRequest? request,
        bool reconcileOnly,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsContinuationRequest(request, reconcileOnly))
        {
            return Continuation(GovernedLoopWakeContinuationStatus.Conflict, "human-input-continuation-request-invalid");
        }

        var runRead = await ReadRunAsync(request!.Checkpoint.Binding.Execution.RunId, cancellationToken).ConfigureAwait(false);
        if (runRead.Status == HumanInputResponseContinuationRunReadStatus.Unavailable)
        {
            return Continuation(GovernedLoopWakeContinuationStatus.Unavailable, "human-input-run-unavailable");
        }
        if (runRead.Status == HumanInputResponseContinuationRunReadStatus.Invalid)
        {
            return Continuation(GovernedLoopWakeContinuationStatus.Conflict, "human-input-run-invalid");
        }

        var run = runRead.Run;
        if (runRead.Status != HumanInputResponseContinuationRunReadStatus.Found
            || run is null
            || !TryFindCheckpointForWake(run, request.Checkpoint, out var checkpoint, out var activation))
        {
            return Continuation(GovernedLoopWakeContinuationStatus.NotCommitted, "human-input-checkpoint-stale");
        }

        if (TryTerminalReceipt(checkpoint!, request, out var terminalEvidenceHash))
        {
            return await ResumeAcceptedIfRequiredAsync(run, checkpoint!, request, terminalEvidenceHash!, cancellationToken).ConfigureAwait(false);
        }
        if (reconcileOnly)
        {
            return Continuation(GovernedLoopWakeContinuationStatus.NotCommitted, "human-input-terminal-not-found");
        }
        if (run.Status != CustomLoopRunStatus.Waiting
            || run.Frontier?.Payload.Status != GovernedLoopFrontierStatus.Waiting
            || checkpoint!.Posture != GovernedLoopHumanInputWaitingCheckpointPosture.AnsweredNotResumed
            || !TrySelectionReference(checkpoint, out var selection)
            || !string.Equals(selection!.SelectionHash, request.Identity.AuthenticationEvidenceHash, StringComparison.Ordinal))
        {
            return Continuation(GovernedLoopWakeContinuationStatus.Conflict, "human-input-selection-substituted");
        }

        var posture = await ReadExactCurrentPostureAsync(request, cancellationToken).ConfigureAwait(false);
        if (posture == HumanInputResponseContinuationPostureStatus.Unavailable)
        {
            return Continuation(GovernedLoopWakeContinuationStatus.Unavailable, "human-input-posture-unavailable");
        }
        if (posture != HumanInputResponseContinuationPostureStatus.Exact)
        {
            return Continuation(GovernedLoopWakeContinuationStatus.Conflict, "human-input-posture-stale");
        }

        GovernedLoopWaitOrderedContext? context;
        try
        {
            context = await _contexts.ResolveAsync(run, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Continuation(GovernedLoopWakeContinuationStatus.Unavailable, "human-input-context-unavailable");
        }

        if (context is null)
        {
            return Continuation(GovernedLoopWakeContinuationStatus.Unavailable, "human-input-context-unavailable");
        }

        var node = context.Plan.Nodes.ElementAtOrDefault(activation!.PlanOrdinal);
        if (node is null
            || activation is not { Status: GovernedLoopNodeExecutionStatus.Waiting, Attempt: not null, AttemptOperationId: not null }
            || !GovernedLoopSequentialNodeDescriptors.IsHumanInput(node.Descriptor)
            || !string.Equals(node.NodeId, activation.NodeId, StringComparison.Ordinal)
            || !TryNow(run.UpdatedAtUtc, request.PreparedWakeEvidence!.RecordedAtUtc, out var terminalizedAtUtc))
        {
            return Continuation(GovernedLoopWakeContinuationStatus.Conflict, "human-input-frontier-substituted");
        }

        var preview = GovernedLoopSequentialFrontierMachine.CompleteWaitingHumanInput(
            run.Frontier,
            run.SequentialAdapterBinding,
            context.Plan,
            node,
            activation,
            activation.Attempt.Value,
            activation.AttemptOperationId,
            "human-input-terminal-preview",
            "0000000000000000000000000000000000000000000000000000000000000000",
            terminalizedAtUtc);
        if (preview.Status != GovernedLoopSequentialFrontierTransitionStatus.Applied || preview.Frontier is null)
        {
            return Continuation(GovernedLoopWakeContinuationStatus.Conflict, "human-input-frontier-completion-rejected");
        }

        var receiptId = TerminalReceiptPrefix + request.ContinuationOperationId;
        var terminalCheckpoint = Terminalize(checkpoint, receiptId, request.PreparedWakeEvidence.ContentHash, terminalizedAtUtc);
        var lifecycleChanged = run.Status != RunStatus(preview.Frontier.Payload.Status);
        var terminalAggregate = IsTerminalFrontierStatus(preview.Frontier.Payload.Status);
        var baseEvent = TerminalEvent(run, activation, request.ContinuationOperationId, terminalizedAtUtc, lifecycleChanged, terminalAggregate);
        var completed = GovernedLoopSequentialFrontierMachine.CompleteWaitingHumanInput(
            run.Frontier,
            run.SequentialAdapterBinding,
            context.Plan,
            node,
            activation,
            activation.Attempt.Value,
            activation.AttemptOperationId,
            baseEvent.EventId,
            CustomLoopSequentialOutcomeArtifactHash.Compute(baseEvent),
            terminalizedAtUtc);
        if (completed.Status != GovernedLoopSequentialFrontierTransitionStatus.Applied || completed.Frontier is null)
        {
            return Continuation(GovernedLoopWakeContinuationStatus.Conflict, "human-input-frontier-completion-rejected");
        }

        var terminalEvent = AttachSequentialEvidence(baseEvent, run.SequentialAdapterBinding!, completed.Frontier.Payload.Nodes[activation.ActivationOrdinal]);
        var candidate = BuildTerminalRun(run, terminalCheckpoint, completed.Frontier, terminalEvent, terminalizedAtUtc);
        var terminalValidation = CustomLoopRunValidator.ValidateUpdate(run, candidate);
        if (!terminalValidation.IsValid)
        {
            return Continuation(GovernedLoopWakeContinuationStatus.Conflict, "human-input-terminal-" + terminalValidation.Errors[0].Code);
        }

        var terminalUpdate = await UpdateAsync(run, candidate, cancellationToken).ConfigureAwait(false);
        if (terminalUpdate.Status == HumanInputResponseContinuationRunUpdateStatus.Unavailable)
        {
            return Continuation(GovernedLoopWakeContinuationStatus.Unavailable, "human-input-terminal-cas-unknown");
        }
        if (terminalUpdate.Status == HumanInputResponseContinuationRunUpdateStatus.Invalid)
        {
            return Continuation(GovernedLoopWakeContinuationStatus.Conflict, "human-input-terminal-cas-invalid");
        }
        if (terminalUpdate.Status == HumanInputResponseContinuationRunUpdateStatus.NotFound)
        {
            return Continuation(GovernedLoopWakeContinuationStatus.NotCommitted, "human-input-terminal-cas-notfound");
        }

        var persisted = terminalUpdate.Run;
        if (persisted is null)
        {
            return Continuation(GovernedLoopWakeContinuationStatus.Conflict, "human-input-terminal-cas-conflict");
        }
        if (!TryFindCheckpointForWake(persisted, request.Checkpoint, out var persistedCheckpoint, out _)
            || !TryTerminalReceipt(persistedCheckpoint!, request, out terminalEvidenceHash))
        {
            return Continuation(GovernedLoopWakeContinuationStatus.Conflict, "human-input-terminal-cas-conflict");
        }
        return await ResumeAcceptedIfRequiredAsync(persisted, persistedCheckpoint!, request, terminalEvidenceHash!, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GovernedLoopWakeContinuationResult> ResumeAcceptedIfRequiredAsync(
        CustomLoopRunRecord run,
        GovernedLoopHumanInputWaitingCheckpoint checkpoint,
        GovernedLoopWakeContinuationRequest request,
        string terminalEvidenceHash,
        CancellationToken cancellationToken)
    {
        if (run.Status != CustomLoopRunStatus.Running)
        {
            return Continuation(GovernedLoopWakeContinuationStatus.Committed, terminalEvidenceHash);
        }

        var activation = run.Frontier?.Payload.Nodes.ElementAtOrDefault(checkpoint.Binding.ActivationOrdinal);
        if (run.Frontier?.Payload.Status != GovernedLoopFrontierStatus.Active
            || activation is not { Descriptor.Kind: GovernedLoopNodeKind.HumanInput, Status: GovernedLoopNodeExecutionStatus.Completed })
        {
            return Continuation(GovernedLoopWakeContinuationStatus.Conflict, "human-input-terminal-frontier-substituted");
        }

        GovernedLoopWaitOrderedContext? context;
        try
        {
            context = await _contexts.ResolveAsync(run, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Continuation(GovernedLoopWakeContinuationStatus.Ambiguous, "human-input-ordered-context-unknown");
        }

        if (context is null)
        {
            return Continuation(GovernedLoopWakeContinuationStatus.Ambiguous, "human-input-ordered-context-unknown");
        }

        try
        {
            var ordered = await _orderedRuntime.ResumeHumanInputAsync(
                new GovernedLoopSequentialOrderedHumanInputResumeRequest(
                    GovernedLoopSequentialOrderedHumanInputResumeRequest.CurrentSchemaVersion,
                    context.Anchor,
                    context.Plan,
                    context.Artifact,
                    checkpoint.Binding.CheckpointId,
                    request.PreparedWakeEvidence!.ContentHash,
                    run.AdmissionActor),
                cancellationToken).ConfigureAwait(false);
            if (ordered is null)
            {
                return Continuation(GovernedLoopWakeContinuationStatus.Ambiguous, "human-input-ordered-reentry-unknown");
            }

            var reconciled = await ReadRunAsync(run.Id, cancellationToken).ConfigureAwait(false);
            return reconciled.Status == HumanInputResponseContinuationRunReadStatus.Found
                && HasAdvancedFromReentryPosture(run, reconciled.Run)
                ? Continuation(GovernedLoopWakeContinuationStatus.Committed, terminalEvidenceHash)
                : Continuation(GovernedLoopWakeContinuationStatus.Ambiguous, "human-input-ordered-reentry-unresolved");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Continuation(GovernedLoopWakeContinuationStatus.Ambiguous, "human-input-ordered-reentry-unknown");
        }
    }

    private async Task<HumanInputResponseContinuationWakeResult> ReplayTerminalWakeAsync(
        CustomLoopRunRecord run,
        GovernedLoopHumanInputWaitingCheckpoint checkpoint,
        GovernedLoopNodeExecutionEvidence activation,
        GovernedLoopSleepService sleep,
        CancellationToken cancellationToken)
    {
        if (!TrySelectionReference(checkpoint, out var selection)
            || !TryCreatePublication(run, checkpoint, activation, out var publication)
            || !TryCreatePublishedCheckpoint(publication, out var sleepCheckpoint)
            || !TryCreateWakeReconciliationRequest(sleepCheckpoint!, selection!, out var reconciliation))
        {
            return Wake(HumanInputResponseContinuationWakeStatus.Invalid);
        }

        try
        {
            var result = await sleep.ReconcileAsync(reconciliation!, cancellationToken).ConfigureAwait(false);
            return result.Status switch
            {
                GovernedLoopWakeResultStatus.Committed or GovernedLoopWakeResultStatus.Duplicate
                    => Wake(HumanInputResponseContinuationWakeStatus.Replayed, result),
                GovernedLoopWakeResultStatus.Unavailable or GovernedLoopWakeResultStatus.AmbiguousAttempt
                    => Wake(HumanInputResponseContinuationWakeStatus.Unavailable, result),
                GovernedLoopWakeResultStatus.Invalid
                    or GovernedLoopWakeResultStatus.Conflict
                    or GovernedLoopWakeResultStatus.Failed
                    or GovernedLoopWakeResultStatus.NotFound
                    => Wake(HumanInputResponseContinuationWakeStatus.Invalid, result),
                _ => Wake(HumanInputResponseContinuationWakeStatus.Stale, result),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Wake(HumanInputResponseContinuationWakeStatus.Unavailable);
        }
    }

    private async Task<bool> ResumeNoResponseIfRequiredAsync(
        CustomLoopRunRecord run,
        GovernedLoopHumanInputWaitingCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        if (!TryFindNoResponseReentry(run, checkpoint.Binding.CheckpointId, out var retained)
            || retained is null)
        {
            return false;
        }

        var retirement = retained.Evidence.LastOrDefault();
        var activation = run.Frontier!.Payload.Nodes[retained.Binding.ActivationOrdinal];
        var events = run.Events.Where(item => string.Equals(item.EventId, activation.OutcomeEvidenceId, StringComparison.Ordinal)).Take(2).ToArray();
        if (retirement is null
            || activation.OutcomeEvidenceId is null
            || events is not [{ SequentialNodeEvidence: { Kind: CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection, FailureEvidenceHash: not null } evidence, FailureEvidence: not null } retirementEvent]
            || !string.Equals(retirementEvent.FailureEvidence.ContentHash, evidence.FailureEvidenceHash, StringComparison.Ordinal))
        {
            return false;
        }

        GovernedLoopWaitOrderedContext? context;
        try
        {
            context = await _contexts.ResolveAsync(run, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }

        if (context is null)
        {
            return false;
        }

        try
        {
            var ordered = await _orderedRuntime.ResumeHumanInputFailureAsync(
                new GovernedLoopSequentialOrderedHumanInputFailureResumeRequest(
                    GovernedLoopSequentialOrderedHumanInputFailureResumeRequest.CurrentSchemaVersion,
                    context.Anchor,
                    context.Plan,
                    context.Artifact,
                    retained.Binding.CheckpointId,
                    retirement.EvidenceHash,
                    retirementEvent.EventId,
                    evidence.FailureEvidenceHash,
                    run.AdmissionActor),
                cancellationToken).ConfigureAwait(false);
            if (ordered is null)
            {
                return false;
            }

            var reconciled = await ReadRunAsync(run.Id, cancellationToken).ConfigureAwait(false);
            return reconciled.Status == HumanInputResponseContinuationRunReadStatus.Found
                && HasAdvancedFromReentryPosture(run, reconciled.Run);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async Task<SelectionAttachment> AttachSelectionAsync(HumanInputResponseContinuationCandidate candidate, CancellationToken cancellationToken)
    {
        var runRead = await ReadRunAsync(candidate.RunId, cancellationToken).ConfigureAwait(false);
        if (runRead.Status == HumanInputResponseContinuationRunReadStatus.Invalid)
        {
            return SelectionAttachment.Invalid();
        }
        if (runRead.Status == HumanInputResponseContinuationRunReadStatus.Unavailable)
        {
            return SelectionAttachment.Unavailable();
        }

        var run = runRead.Run;
        if (runRead.Status != HumanInputResponseContinuationRunReadStatus.Found
            || run is null
            || !TryFindWaitingCheckpoint(run, candidate.CheckpointId, out var checkpoint, out _))
        {
            return SelectionAttachment.Stale();
        }
        if (checkpoint!.Posture == GovernedLoopHumanInputWaitingCheckpointPosture.AnsweredNotResumed
            && TrySelectionReference(checkpoint, out var retained))
        {
            return SelectionAttachment.Replayed(retained!);
        }
        if (checkpoint.Posture != GovernedLoopHumanInputWaitingCheckpointPosture.Pending)
        {
            return SelectionAttachment.Stale();
        }

        var response = await ReadResponseAsync(checkpoint.Request, cancellationToken).ConfigureAwait(false);
        if (response is null || response.Status != HumanInputResponseLifecycleStoreReadStatus.Ready)
        {
            return SelectionAttachment.Unavailable();
        }

        if (response.Snapshot is null)
        {
            return SelectionAttachment.Invalid();
        }

        var expectedRequest = new HumanInputRequestReference(
            HumanInputRequestReference.CurrentSchemaVersion,
            checkpoint.Request.RequestId,
            checkpoint.Request.RequestVersionId,
            checkpoint.Request.RequestHash);
        if (!HumanInputResponseLifecycleStoreSnapshotGuard.TryCapture(response.Snapshot, expectedRequest, out var snapshot)
            || snapshot is null)
        {
            return SelectionAttachment.Invalid();
        }

        var selected = snapshot.Selection;
        if (selected is null)
        {
            return await RetireNoResponseAsync(run, checkpoint, snapshot, cancellationToken).ConfigureAwait(false);
        }
        if (!TrySelectionReference(checkpoint.Request, selected, out var selection))
        {
            return SelectionAttachment.Invalid();
        }
        if (!SelectionMatches(checkpoint, selection!, selected)
            || !TryNow(run.UpdatedAtUtc, selected.SelectedAtUtc, out var attachedAtUtc))
        {
            return SelectionAttachment.Invalid();
        }

        var answered = Answer(checkpoint, selection!, selected.SelectedAtUtc);
        var candidateRun = run with
        {
            LifecycleVersion = checked(run.LifecycleVersion + 1),
            UpdatedAtUtc = attachedAtUtc,
            HumanInputWaitingCheckpoints = ReplaceCheckpoint(run.HumanInputWaitingCheckpoints, answered),
        };
        if (!CustomLoopRunValidator.ValidateUpdate(run, candidateRun).IsValid)
        {
            return SelectionAttachment.Invalid();
        }

        var selectionUpdate = await UpdateAsync(run, candidateRun, cancellationToken).ConfigureAwait(false);
        if (selectionUpdate.Status == HumanInputResponseContinuationRunUpdateStatus.Invalid)
        {
            return SelectionAttachment.Invalid();
        }
        if (selectionUpdate.Status == HumanInputResponseContinuationRunUpdateStatus.Unavailable)
        {
            return SelectionAttachment.Unavailable();
        }

        var persisted = selectionUpdate.Run;
        if (persisted is null || !TryFindWaitingCheckpoint(persisted, candidate.CheckpointId, out var persistedCheckpoint, out _))
        {
            return SelectionAttachment.Unavailable();
        }
        return persistedCheckpoint!.Posture == GovernedLoopHumanInputWaitingCheckpointPosture.AnsweredNotResumed
            && TrySelectionReference(persistedCheckpoint, out var persistedSelection)
            && Equals(persistedSelection, selection)
            ? SelectionAttachment.Attached(selection!)
            : SelectionAttachment.Stale();
    }

    private async Task<SelectionAttachment> RetireNoResponseAsync(
        CustomLoopRunRecord run,
        GovernedLoopHumanInputWaitingCheckpoint checkpoint,
        HumanInputResponseLifecycleStoreSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (!TryExactNoResponseLifecycle(snapshot, checkpoint, out var disposition, out var retiredAtUtc))
        {
            return SelectionAttachment.Invalid();
        }
        if (disposition == NoResponseDisposition.Pending)
        {
            return SelectionAttachment.NoWork();
        }
        if (disposition is not (NoResponseDisposition.Expired or NoResponseDisposition.Cancelled or NoResponseDisposition.Rejected or NoResponseDisposition.SupersessionUnresolved)
            || retiredAtUtc < run.UpdatedAtUtc
            || retiredAtUtc < checkpoint.Evidence[^1].OccurredAtUtc)
        {
            return SelectionAttachment.Invalid();
        }

        var activation = run.Frontier?.Payload.Nodes.ElementAtOrDefault(checkpoint.Binding.ActivationOrdinal);
        GovernedLoopWaitOrderedContext? context;
        try
        {
            context = await _contexts.ResolveAsync(run, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return SelectionAttachment.Unavailable();
        }

        var node = context?.Plan.Nodes.ElementAtOrDefault(activation?.PlanOrdinal ?? -1);
        if (activation is not { Status: GovernedLoopNodeExecutionStatus.Waiting, Attempt: not null, AttemptOperationId: not null }
            || node is null
            || !GovernedLoopSequentialNodeDescriptors.IsHumanInput(node.Descriptor)
            || !string.Equals(node.NodeId, activation.NodeId, StringComparison.Ordinal))
        {
            return SelectionAttachment.Invalid();
        }

        if (!TryPrepareNoResponseTransition(run, context!, node, activation, checkpoint, snapshot.Request.Head, disposition, retiredAtUtc, out var retiredCheckpoint, out var frontier, out var retirementEvent))
        {
            return SelectionAttachment.Invalid();
        }

        var candidate = BuildTerminalRun(
            run,
            retiredCheckpoint!,
            frontier!,
            retirementEvent!,
            retiredAtUtc,
            FailureCode(disposition),
            FailureDetail(disposition));
        if (!CustomLoopRunValidator.ValidateUpdate(run, candidate).IsValid)
        {
            return SelectionAttachment.Invalid();
        }

        var retirementUpdate = await UpdateAsync(run, candidate, cancellationToken).ConfigureAwait(false);
        if (retirementUpdate.Status == HumanInputResponseContinuationRunUpdateStatus.Invalid)
        {
            return SelectionAttachment.Invalid();
        }
        if (retirementUpdate.Status == HumanInputResponseContinuationRunUpdateStatus.Unavailable)
        {
            return SelectionAttachment.Unavailable();
        }

        var persisted = retirementUpdate.Run;
        if (persisted is null)
        {
            return SelectionAttachment.Unavailable();
        }

        if (retiredCheckpoint is null)
        {
            return SelectionAttachment.Invalid();
        }

        var retained = persisted.HumanInputWaitingCheckpoints.SingleOrDefault(item => string.Equals(item.Binding.CheckpointId, checkpoint.Binding.CheckpointId, StringComparison.Ordinal));
        if (retained?.Posture != retiredCheckpoint.Posture
            || !Equals(retained.Evidence.LastOrDefault(), retiredCheckpoint.Evidence.LastOrDefault()))
        {
            return SelectionAttachment.Stale();
        }

        if (TryFindNoResponseReentry(persisted, retained.Binding.CheckpointId, out _))
        {
            return await ResumeNoResponseIfRequiredAsync(persisted, retained, cancellationToken).ConfigureAwait(false)
                ? SelectionAttachment.Retired()
                : SelectionAttachment.Unavailable();
        }

        return HasConvergedNoResponseRetirement(persisted, retained)
            ? SelectionAttachment.Retired()
            : SelectionAttachment.Unavailable();
    }

    private static bool TryPrepareNoResponseTransition(
        CustomLoopRunRecord run,
        GovernedLoopWaitOrderedContext context,
        GovernedLoopSequentialPlanNode node,
        GovernedLoopNodeExecutionEvidence activation,
        GovernedLoopHumanInputWaitingCheckpoint checkpoint,
        HumanInputRequestLifecycleHead lifecycle,
        NoResponseDisposition disposition,
        DateTimeOffset retiredAtUtc,
        out GovernedLoopHumanInputWaitingCheckpoint? retiredCheckpoint,
        out GovernedLoopFrontierPosture? frontier,
        out CustomLoopRunEvent? retirementEvent)
    {
        retiredCheckpoint = null;
        frontier = null;
        retirementEvent = null;
        if (run.SequentialAdapterBinding is null
            || lifecycle.LastOperationId is null
            || !CustomLoopArtifactIdentifier.IsValid(lifecycle.LastOperationId)
            || !TryPreviewNoResponseFrontier(run, context, node, activation, disposition, retiredAtUtc, out var preview)
            || preview is null)
        {
            return false;
        }

        var lifecycleChanged = run.Status != RunStatus(preview.Payload.Status);
        var baseEvent = RetirementEvent(run, activation, lifecycle, disposition, retiredAtUtc, lifecycleChanged, IsTerminalFrontierStatus(preview.Payload.Status));
        if (disposition == NoResponseDisposition.Cancelled)
        {
            var cancelled = GovernedLoopSequentialFrontierMachine.CancelCurrent(run.Frontier, run.SequentialAdapterBinding, retiredAtUtc);
            if (cancelled.Status != GovernedLoopSequentialFrontierTransitionStatus.Applied || cancelled.Frontier is null)
            {
                return false;
            }

            retiredCheckpoint = Retire(checkpoint, disposition, retiredAtUtc);
            frontier = cancelled.Frontier;
            retirementEvent = baseEvent;
            return true;
        }

        if (!TryClassifyNoResponseFailure(run, activation, checkpoint, lifecycle, baseEvent, disposition, out var classified))
        {
            return false;
        }

        GovernedLoopSequentialFrontierTransitionResult transition;
        if (disposition == NoResponseDisposition.SupersessionUnresolved)
        {
            transition = GovernedLoopSequentialFrontierMachine.ReviewBlockWaiting(
                run.Frontier,
                run.SequentialAdapterBinding,
                context.Plan,
                activation,
                classified!.EventId,
                CustomLoopSequentialOutcomeArtifactHash.Compute(classified),
                retiredAtUtc);
        }
        else
        {
            transition = GovernedLoopSequentialFrontierMachine.FailWaiting(
                run.Frontier,
                run.SequentialAdapterBinding,
                context.Plan,
                node,
                activation,
                activation.Attempt!.Value,
                activation.AttemptOperationId,
                classified!.EventId,
                CustomLoopSequentialOutcomeArtifactHash.Compute(classified),
                GovernedLoopControlCondition.Failure,
                retiredAtUtc);
        }
        if (transition.Status != GovernedLoopSequentialFrontierTransitionStatus.Applied || transition.Frontier is null)
        {
            return false;
        }

        var terminalActivation = transition.Frontier.Payload.Nodes[activation.ActivationOrdinal];
        retirementEvent = disposition == NoResponseDisposition.SupersessionUnresolved
            ? AttachSequentialReviewEvidence(classified!, run.SequentialAdapterBinding, terminalActivation, classified!.FailureEvidence!)
            : AttachSequentialFailureEvidence(classified!, run.SequentialAdapterBinding, terminalActivation, classified!.FailureEvidence!);
        retiredCheckpoint = Retire(checkpoint, disposition, retiredAtUtc);
        frontier = transition.Frontier;
        return true;
    }

    private static bool TryPreviewNoResponseFrontier(
        CustomLoopRunRecord run,
        GovernedLoopWaitOrderedContext context,
        GovernedLoopSequentialPlanNode node,
        GovernedLoopNodeExecutionEvidence activation,
        NoResponseDisposition disposition,
        DateTimeOffset retiredAtUtc,
        out GovernedLoopFrontierPosture? frontier)
    {
        const string PreviewHash = "0000000000000000000000000000000000000000000000000000000000000000";
        var previewEventId = "human-input-" + FailureCode(disposition) + "-preview";
        var transition = disposition == NoResponseDisposition.Cancelled
            ? GovernedLoopSequentialFrontierMachine.CancelCurrent(run.Frontier, run.SequentialAdapterBinding, retiredAtUtc)
            : disposition == NoResponseDisposition.SupersessionUnresolved
            ? GovernedLoopSequentialFrontierMachine.ReviewBlockWaiting(
                run.Frontier,
                run.SequentialAdapterBinding,
                context.Plan,
                activation,
                previewEventId,
                PreviewHash,
                retiredAtUtc)
            : GovernedLoopSequentialFrontierMachine.FailWaiting(
                run.Frontier,
                run.SequentialAdapterBinding,
                context.Plan,
                node,
                activation,
                activation.Attempt!.Value,
                activation.AttemptOperationId,
                previewEventId,
                PreviewHash,
                GovernedLoopControlCondition.Failure,
                retiredAtUtc);
        frontier = transition.Frontier;
        return transition.Status == GovernedLoopSequentialFrontierTransitionStatus.Applied && frontier is not null;
    }

    private static bool TryClassifyNoResponseFailure(
        CustomLoopRunRecord run,
        GovernedLoopNodeExecutionEvidence activation,
        GovernedLoopHumanInputWaitingCheckpoint checkpoint,
        HumanInputRequestLifecycleHead lifecycle,
        CustomLoopRunEvent baseEvent,
        NoResponseDisposition disposition,
        out CustomLoopRunEvent? classified)
    {
        classified = null;
        var binding = run.SequentialAdapterBinding;
        if (binding is null
            || activation.Attempt is null
            || !CustomLoopArtifactIdentifier.IsValid(lifecycle.LastOperationId)
            || !Equals(lifecycle.CurrentRequest, new HumanInputRequestReference(
                HumanInputRequestReference.CurrentSchemaVersion,
                checkpoint.Request.RequestId,
                checkpoint.Request.RequestVersionId,
                checkpoint.Request.RequestHash)))
        {
            return false;
        }

        var causalEvidence = new GovernedLoopFailureEvidenceReference(lifecycle.LastOperationId, checkpoint.Request.RequestHash);

        var observation = disposition switch
        {
            NoResponseDisposition.Expired => new GovernedLoopFailureObservation(
                GovernedLoopFailureObservationKind.DeadlineExhausted,
                GovernedLoopFailureSource.Wait,
                FailureCode(disposition),
                causalEvidence),
            NoResponseDisposition.Cancelled => new GovernedLoopFailureObservation(
                GovernedLoopFailureObservationKind.CancellationNoEffect,
                GovernedLoopFailureSource.User,
                FailureCode(disposition),
                causalEvidence),
            NoResponseDisposition.Rejected => new GovernedLoopFailureObservation(
                GovernedLoopFailureObservationKind.TerminalFailure,
                GovernedLoopFailureSource.User,
                FailureCode(disposition),
                causalEvidence),
            NoResponseDisposition.SupersessionUnresolved => new GovernedLoopFailureObservation(
                GovernedLoopFailureObservationKind.EvidenceIntegrityFailure,
                GovernedLoopFailureSource.Evidence,
                FailureCode(disposition),
                causalEvidence),
            _ => null,
        };
        if (observation is null)
        {
            return false;
        }

        var eventHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(baseEvent.EventId)));
        var result = new GovernedLoopFailureClassifier().Classify(
            new GovernedLoopFailureClassificationContext(
                "failure-" + eventHash[..24],
                binding.WorkspaceId,
                binding.ExecutionBinding.RunId,
                binding.ExecutionBinding.Revision,
                binding.ExecutionBinding.ExecutionGeneration,
                activation.ActivationOrdinal,
                activation.VisitOrdinal,
                activation.NodeId,
                activation.Attempt!.Value,
                observation.CausalEvidence),
            [observation],
            baseEvent.TimestampUtc);
        if (result.Evidence is null
            || result.Status != (disposition == NoResponseDisposition.SupersessionUnresolved
                ? GovernedLoopFailureClassificationStatus.ReviewBlocked
                : GovernedLoopFailureClassificationStatus.Classified))
        {
            return false;
        }

        classified = baseEvent with { FailureEvidence = result.Evidence };
        return true;
    }

    private async Task<HumanInputResponseContinuationPostureStatus> ReadExactCurrentPostureAsync(
        GovernedLoopWakeContinuationRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ExpectedPostureHash is null || !TryNow(out var startedAtUtc))
        {
            return HumanInputResponseContinuationPostureStatus.Unavailable;
        }

        GovernedLoopSleepCurrentPostureReadResult? read;
        try
        {
            read = await _currentPosture.ReadAsync(request.Checkpoint.Binding.Execution, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return HumanInputResponseContinuationPostureStatus.Unavailable;
        }

        if (!TryNow(out var completedAtUtc)
            || read is null
            || read.Status == GovernedLoopSleepCurrentPostureReadStatus.Unavailable)
        {
            return HumanInputResponseContinuationPostureStatus.Unavailable;
        }

        return read.Status == GovernedLoopSleepCurrentPostureReadStatus.Found
            && read.Posture is { } posture
            && GovernedLoopSleepPosturePolicy.IsWellFormed(posture, request.Checkpoint.Binding.Execution, startedAtUtc, completedAtUtc)
            && string.Equals(posture.PostureHash, request.ExpectedPostureHash, StringComparison.Ordinal)
            && GovernedLoopSleepPosturePolicy.EvaluateWake(posture, request.Checkpoint, completedAtUtc) == GovernedLoopSleepPostureDecision.Eligible
            ? HumanInputResponseContinuationPostureStatus.Exact
            : HumanInputResponseContinuationPostureStatus.Conflict;
    }

    private async Task<HumanInputResponseContinuationWakeResolution> ReadRunBySleepCheckpointAsync(
        GovernedLoopAuthenticatedWakeVerificationRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryParseEventReference(request.AuthenticatedEventReference, out _))
        {
            return new HumanInputResponseContinuationWakeResolution(HumanInputResponseContinuationWakeResolutionStatus.Invalid);
        }

        try
        {
            var read = await _sleepStore.ReadCheckpointAsync(request.CheckpointId, cancellationToken).ConfigureAwait(false);
            if (read is null)
            {
                return new HumanInputResponseContinuationWakeResolution(HumanInputResponseContinuationWakeResolutionStatus.Invalid);
            }

            if (read.Status == GovernedLoopSleepStoreReadStatus.NotFound)
            {
                return new HumanInputResponseContinuationWakeResolution(HumanInputResponseContinuationWakeResolutionStatus.NotFound);
            }
            if (read.Status is GovernedLoopSleepStoreReadStatus.Unavailable or GovernedLoopSleepStoreReadStatus.Conflict)
            {
                return new HumanInputResponseContinuationWakeResolution(HumanInputResponseContinuationWakeResolutionStatus.Unavailable);
            }
            if (read.Status != GovernedLoopSleepStoreReadStatus.Found
                || read.Checkpoint is not { } checkpoint
                || !GovernedLoopSleepContractValidator.Validate(checkpoint).IsValid)
            {
                return new HumanInputResponseContinuationWakeResolution(HumanInputResponseContinuationWakeResolutionStatus.Invalid);
            }
            if (!string.Equals(checkpoint.ContentHash, request.CheckpointHash, StringComparison.Ordinal)
                || checkpoint.WakeMode != GovernedLoopWakeMode.AuthenticatedEvent
                || !string.Equals(checkpoint.AuthenticatedEventReference, request.AuthenticatedEventReference, StringComparison.Ordinal))
            {
                return new HumanInputResponseContinuationWakeResolution(HumanInputResponseContinuationWakeResolutionStatus.NotFound);
            }

            var run = await ReadRunAsync(checkpoint.Binding.Execution.RunId, cancellationToken).ConfigureAwait(false);
            return run.Status switch
            {
                HumanInputResponseContinuationRunReadStatus.Found => new HumanInputResponseContinuationWakeResolution(
                    HumanInputResponseContinuationWakeResolutionStatus.Found,
                    run.Run,
                    checkpoint),
                HumanInputResponseContinuationRunReadStatus.NotFound => new HumanInputResponseContinuationWakeResolution(HumanInputResponseContinuationWakeResolutionStatus.NotFound),
                HumanInputResponseContinuationRunReadStatus.Invalid => new HumanInputResponseContinuationWakeResolution(HumanInputResponseContinuationWakeResolutionStatus.Invalid),
                _ => new HumanInputResponseContinuationWakeResolution(HumanInputResponseContinuationWakeResolutionStatus.Unavailable),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new HumanInputResponseContinuationWakeResolution(HumanInputResponseContinuationWakeResolutionStatus.Unavailable);
        }
    }

    private async Task<HumanInputResponseContinuationRunReadResult> ReadRunAsync(string runId, CancellationToken cancellationToken)
    {
        try
        {
            var run = await _runs.GetAsync(runId, cancellationToken).ConfigureAwait(false);
            return run is null
                ? new HumanInputResponseContinuationRunReadResult(HumanInputResponseContinuationRunReadStatus.NotFound)
                : CustomLoopRunValidator.Validate(run).IsValid
                    ? new HumanInputResponseContinuationRunReadResult(HumanInputResponseContinuationRunReadStatus.Found, run)
                    : new HumanInputResponseContinuationRunReadResult(HumanInputResponseContinuationRunReadStatus.Invalid);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FormatException)
        {
            return new HumanInputResponseContinuationRunReadResult(HumanInputResponseContinuationRunReadStatus.Invalid);
        }
        catch
        {
            return new HumanInputResponseContinuationRunReadResult(HumanInputResponseContinuationRunReadStatus.Unavailable);
        }
    }

    private async Task<HumanInputResponseContinuationRunUpdateResult> UpdateAsync(
        CustomLoopRunRecord current,
        CustomLoopRunRecord candidate,
        CancellationToken cancellationToken)
    {
        var mutationUnavailable = false;
        try
        {
            var result = await _runs.UpdateAsync(candidate, current.LifecycleVersion, cancellationToken).ConfigureAwait(false);
            if (result.Status == CustomLoopRunStoreStatus.Updated && result.Run is not null)
            {
                return CustomLoopRunValidator.Validate(result.Run).IsValid
                    ? new HumanInputResponseContinuationRunUpdateResult(HumanInputResponseContinuationRunUpdateStatus.Updated, result.Run)
                    : new HumanInputResponseContinuationRunUpdateResult(HumanInputResponseContinuationRunUpdateStatus.Invalid);
            }
            if (result.Status == CustomLoopRunStoreStatus.NotFound)
            {
                return new HumanInputResponseContinuationRunUpdateResult(HumanInputResponseContinuationRunUpdateStatus.NotFound);
            }
            if (result.Status is CustomLoopRunStoreStatus.Conflict
                or CustomLoopRunStoreStatus.OperationConflict
                or CustomLoopRunStoreStatus.TerminalImmutable
                or CustomLoopRunStoreStatus.DeletedIdentityConflict)
            {
                var rereadConflict = await ReadRunAsync(current.Id, CancellationToken.None).ConfigureAwait(false);
                return rereadConflict.Status switch
                {
                    HumanInputResponseContinuationRunReadStatus.Found when rereadConflict.Run!.LifecycleVersion > current.LifecycleVersion
                        => new HumanInputResponseContinuationRunUpdateResult(HumanInputResponseContinuationRunUpdateStatus.Reconciled, rereadConflict.Run),
                    HumanInputResponseContinuationRunReadStatus.Found
                        => new HumanInputResponseContinuationRunUpdateResult(HumanInputResponseContinuationRunUpdateStatus.Conflict, rereadConflict.Run),
                    HumanInputResponseContinuationRunReadStatus.NotFound
                        => new HumanInputResponseContinuationRunUpdateResult(HumanInputResponseContinuationRunUpdateStatus.NotFound),
                    HumanInputResponseContinuationRunReadStatus.Invalid
                        => new HumanInputResponseContinuationRunUpdateResult(HumanInputResponseContinuationRunUpdateStatus.Invalid),
                    _ => new HumanInputResponseContinuationRunUpdateResult(HumanInputResponseContinuationRunUpdateStatus.Unavailable),
                };
            }

            mutationUnavailable = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FormatException)
        {
            return new HumanInputResponseContinuationRunUpdateResult(HumanInputResponseContinuationRunUpdateStatus.Invalid);
        }
        catch
        {
            mutationUnavailable = true;
        }

        var reread = await ReadRunAsync(current.Id, CancellationToken.None).ConfigureAwait(false);
        return reread.Status switch
        {
            HumanInputResponseContinuationRunReadStatus.Found when reread.Run!.LifecycleVersion > current.LifecycleVersion
                => new HumanInputResponseContinuationRunUpdateResult(HumanInputResponseContinuationRunUpdateStatus.Reconciled, reread.Run),
            HumanInputResponseContinuationRunReadStatus.Found when mutationUnavailable
                => new HumanInputResponseContinuationRunUpdateResult(HumanInputResponseContinuationRunUpdateStatus.Unavailable, reread.Run),
            HumanInputResponseContinuationRunReadStatus.Found
                => new HumanInputResponseContinuationRunUpdateResult(HumanInputResponseContinuationRunUpdateStatus.Conflict, reread.Run),
            HumanInputResponseContinuationRunReadStatus.NotFound
                => new HumanInputResponseContinuationRunUpdateResult(HumanInputResponseContinuationRunUpdateStatus.NotFound),
            HumanInputResponseContinuationRunReadStatus.Invalid
                => new HumanInputResponseContinuationRunUpdateResult(HumanInputResponseContinuationRunUpdateStatus.Invalid),
            _ => new HumanInputResponseContinuationRunUpdateResult(HumanInputResponseContinuationRunUpdateStatus.Unavailable),
        };
    }

    private async Task<HumanInputResponseLifecycleStoreReadResult?> ReadResponseAsync(HumanInputRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await _responses.ReadAsync(new HumanInputRequestReference(
                HumanInputRequestReference.CurrentSchemaVersion,
                request.RequestId,
                request.RequestVersionId,
                request.RequestHash), cancellationToken).ConfigureAwait(false);
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

    private static bool TryCreatePublication(
        CustomLoopRunRecord run,
        GovernedLoopHumanInputWaitingCheckpoint checkpoint,
        GovernedLoopNodeExecutionEvidence activation,
        out GovernedLoopSleepPublicationRequest publication)
    {
        publication = null!;
        if (run.SequentialAdapterBinding is not { } binding
            || activation.Attempt is not { } attempt
            || activation.AttemptOperationId is null
            || checkpoint.Request.Timing is null)
        {
            return false;
        }

        try
        {
            publication = new GovernedLoopSleepPublicationRequest(
                new GovernedLoopSleepBinding(
                    binding.ExecutionBinding,
                    binding.AdmissionReceipt.Intent.Publication,
                    checkpoint.Binding.FrontierVersion,
                    checkpoint.Binding.FrontierHash,
                    checkpoint.Binding.ActivationOrdinal,
                    checkpoint.Binding.CycleId,
                    checkpoint.Binding.CycleIteration,
                    checkpoint.Binding.NodeId,
                    checkpoint.Binding.NodeVisitOrdinal,
                    attempt,
                    activation.AttemptOperationId),
                GovernedLoopWakeMode.AuthenticatedEvent,
                null,
                GovernedLoopHumanInputContinuationVocabulary.AuthenticatedEventReferencePrefix + checkpoint.Binding.CheckpointId,
                checkpoint.Request.Timing.RequestedAtUtc);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryCreatePublishedCheckpoint(
        GovernedLoopSleepPublicationRequest publication,
        out GovernedLoopSleepCheckpoint checkpoint)
    {
        checkpoint = null!;
        var preparedAtUtc = publication.CheckpointPreparedAtUtc;
        if (preparedAtUtc is null || preparedAtUtc.Value == default || preparedAtUtc.Value.Offset != TimeSpan.Zero)
        {
            return false;
        }

        try
        {
            checkpoint = GovernedLoopSleepContractHash.Apply(new GovernedLoopSleepCheckpoint(
                GovernedLoopSleepCheckpoint.CurrentSchemaVersion,
                string.Empty,
                publication.Binding,
                publication.WakeMode,
                publication.WakeDeadlineUtc,
                publication.AuthenticatedEventReference,
                preparedAtUtc.Value,
                string.Empty));
            return GovernedLoopSleepContractValidator.Validate(checkpoint).IsValid;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryCreateWakeReconciliationRequest(
        GovernedLoopSleepCheckpoint checkpoint,
        HumanInputResponseSelectionReference selection,
        out GovernedLoopWakeReconciliationRequest? request)
    {
        request = null;
        if (checkpoint.WakeMode != GovernedLoopWakeMode.AuthenticatedEvent
            || !IsHash(selection.SelectionHash))
        {
            return false;
        }

        try
        {
            var identity = GovernedLoopSleepContractHash.Apply(new GovernedLoopWakeIdentity(
                GovernedLoopWakeIdentity.CurrentSchemaVersion,
                string.Empty,
                checkpoint.CheckpointId,
                checkpoint.ContentHash,
                checkpoint.WakeMode,
                checkpoint.AuthenticatedEventReference,
                selection.SelectionHash,
                string.Empty));
            if (!GovernedLoopSleepContractValidator.ValidateComposition(checkpoint, identity).IsValid)
            {
                return false;
            }

            request = new GovernedLoopWakeReconciliationRequest(checkpoint.CheckpointId, identity.WakeId);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryFindWaitingCheckpoint(
        CustomLoopRunRecord run,
        string checkpointId,
        out GovernedLoopHumanInputWaitingCheckpoint? checkpoint,
        out GovernedLoopNodeExecutionEvidence? activation)
    {
        checkpoint = run.HumanInputWaitingCheckpoints.SingleOrDefault(item => string.Equals(item.Binding.CheckpointId, checkpointId, StringComparison.Ordinal));
        activation = checkpoint is null ? null : run.Frontier?.Payload.Nodes.ElementAtOrDefault(checkpoint.Binding.ActivationOrdinal);
        return !run.IsTerminal
            && run.Status == CustomLoopRunStatus.Waiting
            && run.Frontier?.Payload.Status == GovernedLoopFrontierStatus.Waiting
            && checkpoint is not null
            && checkpoint.Posture is GovernedLoopHumanInputWaitingCheckpointPosture.Pending or GovernedLoopHumanInputWaitingCheckpointPosture.AnsweredNotResumed
            && activation is { Descriptor.Kind: GovernedLoopNodeKind.HumanInput, Status: GovernedLoopNodeExecutionStatus.Waiting, Attempt: not null, AttemptOperationId: not null };
    }

    private static bool TryFindNoResponseReentry(
        CustomLoopRunRecord run,
        string checkpointId,
        out GovernedLoopHumanInputWaitingCheckpoint? checkpoint)
    {
        checkpoint = run.HumanInputWaitingCheckpoints.SingleOrDefault(item => string.Equals(item.Binding.CheckpointId, checkpointId, StringComparison.Ordinal));
        var activation = checkpoint is null ? null : run.Frontier?.Payload.Nodes.ElementAtOrDefault(checkpoint.Binding.ActivationOrdinal);
        var terminal = checkpoint?.Evidence.LastOrDefault();
        return !run.IsTerminal
            && run.Status == CustomLoopRunStatus.Running
            && run.Frontier?.Payload.Status == GovernedLoopFrontierStatus.Active
            && checkpoint?.Posture is GovernedLoopHumanInputWaitingCheckpointPosture.Expired or GovernedLoopHumanInputWaitingCheckpointPosture.Rejected
            && terminal?.Kind is GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Expired or GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Rejected
            && activation is { Descriptor.Kind: GovernedLoopNodeKind.HumanInput, Status: GovernedLoopNodeExecutionStatus.Failed, Attempt: not null, AttemptOperationId: not null, OutcomeEvidenceId: not null, OutcomeEvidenceHash: not null };
    }

    private static bool TryFindAcceptedTerminalReplay(
        CustomLoopRunRecord run,
        string checkpointId,
        out GovernedLoopHumanInputWaitingCheckpoint? checkpoint,
        out GovernedLoopNodeExecutionEvidence? activation)
    {
        checkpoint = run.HumanInputWaitingCheckpoints.SingleOrDefault(item => string.Equals(item.Binding.CheckpointId, checkpointId, StringComparison.Ordinal));
        activation = checkpoint is null ? null : run.Frontier?.Payload.Nodes.ElementAtOrDefault(checkpoint.Binding.ActivationOrdinal);
        return checkpoint?.Posture == GovernedLoopHumanInputWaitingCheckpointPosture.Terminal
            && checkpoint.Evidence.LastOrDefault()?.Kind == GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Terminalized
            && TrySelectionReference(checkpoint, out _)
            && activation is { Descriptor.Kind: GovernedLoopNodeKind.HumanInput, Status: GovernedLoopNodeExecutionStatus.Completed, Attempt: not null, AttemptOperationId: not null };
    }

    private static bool HasConvergedNoResponseRetirement(
        CustomLoopRunRecord run,
        GovernedLoopHumanInputWaitingCheckpoint checkpoint)
    {
        var activation = run.Frontier?.Payload.Nodes.ElementAtOrDefault(checkpoint.Binding.ActivationOrdinal);
        var evidence = checkpoint.Evidence.LastOrDefault();
        if (evidence is null || !IsHash(evidence.EvidenceHash) || !CustomLoopRunValidator.Validate(run).IsValid)
        {
            return false;
        }

        return checkpoint.Posture switch
        {
            GovernedLoopHumanInputWaitingCheckpointPosture.Cancelled
                => evidence.Kind == GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Cancelled
                    && run.Status == CustomLoopRunStatus.Cancelled
                    && run.Frontier?.Payload.Status == GovernedLoopFrontierStatus.Cancelled
                    && activation is { Descriptor.Kind: GovernedLoopNodeKind.HumanInput, Status: GovernedLoopNodeExecutionStatus.Waiting },
            GovernedLoopHumanInputWaitingCheckpointPosture.Expired
                => evidence.Kind == GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Expired
                    && run.Status == CustomLoopRunStatus.Failed
                    && run.Frontier?.Payload.Status == GovernedLoopFrontierStatus.Failed
                    && activation is { Descriptor.Kind: GovernedLoopNodeKind.HumanInput, Status: GovernedLoopNodeExecutionStatus.Failed },
            GovernedLoopHumanInputWaitingCheckpointPosture.Rejected
                => evidence.Kind == GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Rejected
                    && run.Status == CustomLoopRunStatus.Failed
                    && run.Frontier?.Payload.Status == GovernedLoopFrontierStatus.Failed
                    && activation is { Descriptor.Kind: GovernedLoopNodeKind.HumanInput, Status: GovernedLoopNodeExecutionStatus.Failed },
            GovernedLoopHumanInputWaitingCheckpointPosture.NeedsReview
                => evidence.Kind == GovernedLoopHumanInputWaitingCheckpointEvidenceKind.NeedsReview
                    && run.Status == CustomLoopRunStatus.NeedsReview
                    && run.Frontier?.Payload.Status == GovernedLoopFrontierStatus.ReviewBlocked
                    && activation is { Descriptor.Kind: GovernedLoopNodeKind.HumanInput, Status: GovernedLoopNodeExecutionStatus.ReviewBlocked },
            _ => false,
        };
    }

    private static bool HasAdvancedFromReentryPosture(CustomLoopRunRecord prior, CustomLoopRunRecord? current)
    {
        if (current is null)
        {
            return false;
        }

        var priorFrontier = prior.Frontier;
        var currentFrontier = current.Frontier;
        return current.Status != CustomLoopRunStatus.Running
            || currentFrontier?.Payload.Status != GovernedLoopFrontierStatus.Active
            || priorFrontier is not null
                && currentFrontier is not null
                && (currentFrontier.Payload.FrontierVersion > priorFrontier.Payload.FrontierVersion
                    || !string.Equals(currentFrontier.Payload.ContentHash, priorFrontier.Payload.ContentHash, StringComparison.Ordinal));
    }

    private static bool TryFindCheckpointForWake(
        CustomLoopRunRecord run,
        GovernedLoopSleepCheckpoint sleepCheckpoint,
        out GovernedLoopHumanInputWaitingCheckpoint? checkpoint,
        out GovernedLoopNodeExecutionEvidence? activation)
    {
        checkpoint = null;
        activation = null;
        if (!TryParseEventReference(sleepCheckpoint.AuthenticatedEventReference, out var checkpointId))
        {
            return false;
        }

        checkpoint = run.HumanInputWaitingCheckpoints.SingleOrDefault(item => string.Equals(item.Binding.CheckpointId, checkpointId, StringComparison.Ordinal));
        activation = checkpoint is null ? null : run.Frontier?.Payload.Nodes.ElementAtOrDefault(checkpoint.Binding.ActivationOrdinal);
        return checkpoint is not null
            && activation is not null
            && Equals(checkpoint.Binding.Execution, sleepCheckpoint.Binding.Execution)
            && Equals(checkpoint.Binding.Publication, sleepCheckpoint.Binding.Publication)
            && checkpoint.Binding.FrontierVersion == sleepCheckpoint.Binding.FrontierVersion
            && string.Equals(checkpoint.Binding.FrontierHash, sleepCheckpoint.Binding.FrontierHash, StringComparison.Ordinal)
            && checkpoint.Binding.ActivationOrdinal == sleepCheckpoint.Binding.ActivationOrdinal
            && string.Equals(checkpoint.Binding.NodeId, sleepCheckpoint.Binding.NodeId, StringComparison.Ordinal)
            && checkpoint.Binding.NodeVisitOrdinal == sleepCheckpoint.Binding.NodeVisitOrdinal
            && activation.Attempt == sleepCheckpoint.Binding.WaitAttempt
            && string.Equals(activation.AttemptOperationId, sleepCheckpoint.Binding.WaitOperationId, StringComparison.Ordinal);
    }

    private static bool TrySelectionReference(GovernedLoopHumanInputWaitingCheckpoint checkpoint, out HumanInputResponseSelectionReference? reference)
    {
        reference = checkpoint.Evidence.Length > 1 ? checkpoint.Evidence[1].AnswerSelection : null;
        return HumanInputResponseContractValidator.ValidateSelectionReference(reference).IsValid;
    }

    private static bool TrySelectionReference(HumanInputRequest request, HumanInputResponseSelection selection, out HumanInputResponseSelectionReference? reference)
    {
        reference = null;
        if (!HumanInputResponseSelectionHash.Matches(selection)
            || !Equals(selection.Request, new HumanInputRequestReference(HumanInputRequestReference.CurrentSchemaVersion, request.RequestId, request.RequestVersionId, request.RequestHash)))
        {
            return false;
        }

        reference = HumanInputResponseSelectionReference.Create(selection);
        return HumanInputResponseContractValidator.ValidateSelectionReference(reference).IsValid;
    }

    private static bool SelectionMatches(
        GovernedLoopHumanInputWaitingCheckpoint checkpoint,
        HumanInputResponseSelectionReference reference,
        HumanInputResponseSelection selection)
        => HumanInputResponseSelectionHash.Matches(selection)
            && Equals(reference, HumanInputResponseSelectionReference.Create(selection))
            && string.Equals(selection.Request.RequestId, checkpoint.Request.RequestId, StringComparison.Ordinal)
            && string.Equals(selection.Request.RequestVersionId, checkpoint.Request.RequestVersionId, StringComparison.Ordinal)
            && string.Equals(selection.Request.RequestHash, checkpoint.Request.RequestHash, StringComparison.Ordinal)
            && selection.SelectedAtUtc >= checkpoint.Request.Timing.RequestedAtUtc
            && selection.SelectedAtUtc <= checkpoint.Request.Timing.ExpiresAtUtc;

    private static GovernedLoopHumanInputWaitingCheckpoint Answer(
        GovernedLoopHumanInputWaitingCheckpoint checkpoint,
        HumanInputResponseSelectionReference selection,
        DateTimeOffset selectedAtUtc)
    {
        var evidence = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpointEvidence(
            GovernedLoopHumanInputWaitingCheckpoint.CurrentSchemaVersion,
            checkpoint.Evidence.Length + 1,
            GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Answered,
            selectedAtUtc,
            selection,
            null,
            null,
            null,
            null,
            checkpoint.Evidence[^1].EvidenceHash,
            string.Empty));
        return GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpoint(
            checkpoint.SchemaVersion,
            checkpoint.Binding,
            checkpoint.NodeConfiguration,
            checkpoint.ResolvedPolicy,
            checkpoint.Request,
            GovernedLoopHumanInputWaitingCheckpointPosture.AnsweredNotResumed,
            [.. checkpoint.Evidence, evidence],
            string.Empty));
    }

    private static GovernedLoopHumanInputWaitingCheckpoint Retire(
        GovernedLoopHumanInputWaitingCheckpoint checkpoint,
        NoResponseDisposition disposition,
        DateTimeOffset retiredAtUtc)
    {
        var (posture, kind) = disposition switch
        {
            NoResponseDisposition.Expired => (
                GovernedLoopHumanInputWaitingCheckpointPosture.Expired,
                GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Expired),
            NoResponseDisposition.Cancelled => (
                GovernedLoopHumanInputWaitingCheckpointPosture.Cancelled,
                GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Cancelled),
            NoResponseDisposition.Rejected => (
                GovernedLoopHumanInputWaitingCheckpointPosture.Rejected,
                GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Rejected),
            NoResponseDisposition.SupersessionUnresolved => (
                GovernedLoopHumanInputWaitingCheckpointPosture.NeedsReview,
                GovernedLoopHumanInputWaitingCheckpointEvidenceKind.NeedsReview),
            _ => throw new ArgumentOutOfRangeException(nameof(disposition)),
        };
        var evidence = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpointEvidence(
            GovernedLoopHumanInputWaitingCheckpoint.CurrentSchemaVersion,
            checkpoint.Evidence.Length + 1,
            kind,
            retiredAtUtc,
            null,
            null,
            null,
            null,
            null,
            checkpoint.Evidence[^1].EvidenceHash,
            string.Empty));
        return GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpoint(
            checkpoint.SchemaVersion,
            checkpoint.Binding,
            checkpoint.NodeConfiguration,
            checkpoint.ResolvedPolicy,
            checkpoint.Request,
            posture,
            [.. checkpoint.Evidence, evidence],
            string.Empty));
    }

    private static GovernedLoopHumanInputWaitingCheckpoint Terminalize(
        GovernedLoopHumanInputWaitingCheckpoint checkpoint,
        string receiptId,
        string receiptHash,
        DateTimeOffset terminalizedAtUtc)
    {
        var evidence = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpointEvidence(
            GovernedLoopHumanInputWaitingCheckpoint.CurrentSchemaVersion,
            checkpoint.Evidence.Length + 1,
            GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Terminalized,
            terminalizedAtUtc,
            null,
            null,
            null,
            receiptId,
            receiptHash,
            checkpoint.Evidence[^1].EvidenceHash,
            string.Empty));
        return GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpoint(
            checkpoint.SchemaVersion,
            checkpoint.Binding,
            checkpoint.NodeConfiguration,
            checkpoint.ResolvedPolicy,
            checkpoint.Request,
            GovernedLoopHumanInputWaitingCheckpointPosture.Terminal,
            [.. checkpoint.Evidence, evidence],
            string.Empty));
    }

    private static CustomLoopRunRecord BuildTerminalRun(
        CustomLoopRunRecord run,
        GovernedLoopHumanInputWaitingCheckpoint terminalCheckpoint,
        GovernedLoopFrontierPosture frontier,
        CustomLoopRunEvent terminalEvent,
        DateTimeOffset terminalizedAtUtc,
        string? failureCode = null,
        string? failureDetail = null)
    {
        var status = RunStatus(frontier.Payload.Status);
        var running = status == CustomLoopRunStatus.Running;
        var terminal = status is CustomLoopRunStatus.Completed or CustomLoopRunStatus.Failed or CustomLoopRunStatus.Cancelled or CustomLoopRunStatus.NeedsReview;
        var lifecycleChanged = run.Status != status;
        var acceptedResponse = terminalCheckpoint.Posture == GovernedLoopHumanInputWaitingCheckpointPosture.Terminal;
        var events = lifecycleChanged && terminal
            ? [terminalEvent, LifecycleEvent(run, terminalizedAtUtc, acceptedResponse, terminalEvent.EventId, run.Events.Length + 2)]
            : lifecycleChanged
            ? [LifecycleEvent(run, terminalizedAtUtc, acceptedResponse, terminalEvent.EventId, run.Events.Length + 1), terminalEvent]
            : new[] { terminalEvent };
        return run with
        {
            LifecycleVersion = checked(run.LifecycleVersion + 1),
            Status = status,
            UpdatedAtUtc = terminalizedAtUtc,
            CompletedAtUtc = terminal ? terminalizedAtUtc : null,
            ExecutionClock = running ? run.ExecutionClock with { ActiveSinceUtc = terminalizedAtUtc } : run.ExecutionClock with { ActiveSinceUtc = null },
            Frontier = frontier,
            Events = [.. run.Events, .. events],
            FailureCode = status is CustomLoopRunStatus.Failed or CustomLoopRunStatus.NeedsReview ? failureCode : null,
            FailureDetail = status is CustomLoopRunStatus.Failed or CustomLoopRunStatus.NeedsReview ? failureDetail : null,
            FinalOutput = status == CustomLoopRunStatus.Completed ? run.FinalOutput ?? string.Empty : null,
            HumanInputWaitingCheckpoints = ReplaceCheckpoint(run.HumanInputWaitingCheckpoints, terminalCheckpoint),
        };
    }

    private static CustomLoopRunStatus RunStatus(GovernedLoopFrontierStatus status)
        => status switch
        {
            GovernedLoopFrontierStatus.Active => CustomLoopRunStatus.Running,
            GovernedLoopFrontierStatus.Waiting => CustomLoopRunStatus.Waiting,
            GovernedLoopFrontierStatus.Completed => CustomLoopRunStatus.Completed,
            GovernedLoopFrontierStatus.Failed => CustomLoopRunStatus.Failed,
            GovernedLoopFrontierStatus.Cancelled => CustomLoopRunStatus.Cancelled,
            GovernedLoopFrontierStatus.ReviewBlocked => CustomLoopRunStatus.NeedsReview,
            _ => CustomLoopRunStatus.NeedsReview,
        };

    private static bool IsTerminalFrontierStatus(GovernedLoopFrontierStatus status)
        => status is GovernedLoopFrontierStatus.Completed
            or GovernedLoopFrontierStatus.Failed
            or GovernedLoopFrontierStatus.Cancelled
            or GovernedLoopFrontierStatus.ReviewBlocked;

    private static string FailureCode(NoResponseDisposition disposition)
        => disposition switch
        {
            NoResponseDisposition.Expired => "human-input-expired",
            NoResponseDisposition.Cancelled => "human-input-cancelled",
            NoResponseDisposition.Rejected => "human-input-rejected",
            NoResponseDisposition.SupersessionUnresolved => "human-input-supersession-unresolved",
            _ => "human-input-no-response-invalid",
        };

    private static string NoResponseEventId(NoResponseDisposition disposition, string operationId)
    {
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(operationId)));
        return "human-input-" + FailureCode(disposition) + "-" + hash[..24];
    }

    private static string FailureDetail(NoResponseDisposition disposition)
        => disposition switch
        {
            NoResponseDisposition.Expired => "The exact Human Input response window expired before an accepted selection.",
            NoResponseDisposition.Rejected => "The exact Human Input request was rejected before an accepted selection.",
            NoResponseDisposition.SupersessionUnresolved => "The exact Human Input request was superseded without a distinct replacement checkpoint that can be authenticated for automatic continuation.",
            _ => "The exact Human Input request reached an unsupported no-response terminal disposition.",
        };

    private static CustomLoopRunEvent TerminalEvent(
        CustomLoopRunRecord run,
        GovernedLoopNodeExecutionEvidence activation,
        string continuationOperationId,
        DateTimeOffset terminalizedAtUtc,
        bool lifecycleChanged,
        bool lifecycleFollowsTerminal)
        => new(
            run.Events.Length + (lifecycleChanged && !lifecycleFollowsTerminal ? 2 : 1),
            "human-input-terminal-" + continuationOperationId[..32],
            terminalizedAtUtc,
            CustomLoopRunEventKind.NodeAttemptCompleted,
            activation.CycleIteration ?? run.Checkpoint.Iteration,
            activation.NodeId,
            activation.Attempt,
            "The exact accepted Human Input response terminalized its checkpoint before canonical ordered re-entry.",
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

    private static CustomLoopRunEvent RetirementEvent(
        CustomLoopRunRecord run,
        GovernedLoopNodeExecutionEvidence activation,
        HumanInputRequestLifecycleHead lifecycle,
        NoResponseDisposition disposition,
        DateTimeOffset retiredAtUtc,
        bool lifecycleChanged,
        bool lifecycleFollowsTerminal)
        => new(
            run.Events.Length + (lifecycleChanged && !lifecycleFollowsTerminal ? 2 : 1),
            NoResponseEventId(disposition, lifecycle.LastOperationId),
            retiredAtUtc,
            disposition == NoResponseDisposition.Cancelled ? CustomLoopRunEventKind.LifecycleChanged : CustomLoopRunEventKind.NodeAttemptFailed,
            activation.CycleIteration ?? run.Checkpoint.Iteration,
            activation.NodeId,
            activation.Attempt,
            $"The exact Human Input lifecycle operation {lifecycle.LastOperationId} established {FailureCode(disposition)} without an accepted selection.",
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

    private static CustomLoopRunEvent AttachSequentialEvidence(
        CustomLoopRunEvent runEvent,
        GovernedLoopSequentialAdapterBinding binding,
        GovernedLoopNodeExecutionEvidence activation)
    {
        var evidence = CustomLoopSequentialNodeEvidenceHash.Apply(new CustomLoopSequentialNodeEvidence(
            CustomLoopSequentialNodeEvidence.CurrentSchemaVersion,
            CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
            binding.WorkspaceId,
            binding.ExecutionBinding.RunId,
            binding.ExecutionBinding.Revision,
            binding.ExecutionBinding.ExecutionGeneration,
            activation.ActivationOrdinal,
            activation.VisitOrdinal,
            activation.NodeId,
            activation.Attempt,
            activation.CycleId,
            activation.CycleIteration,
            GovernedLoopControlCondition.Success,
            activation.SelectedControlEdgeIds,
            activation.SkippedControlEdgeIds,
            null,
            null,
            CustomLoopSequentialNodeDisposition.Completed,
            CustomLoopSequentialOutcomeArtifactHash.Compute(runEvent),
            string.Empty));
        return runEvent with { SequentialNodeEvidence = evidence };
    }

    private static CustomLoopRunEvent AttachSequentialFailureEvidence(
        CustomLoopRunEvent runEvent,
        GovernedLoopSequentialAdapterBinding binding,
        GovernedLoopNodeExecutionEvidence activation,
        GovernedLoopFailureEvidence failure)
    {
        var evidence = new CustomLoopSequentialNodeEvidence(
            CustomLoopSequentialNodeEvidence.CurrentSchemaVersion,
            CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection,
            binding.WorkspaceId,
            binding.ExecutionBinding.RunId,
            binding.ExecutionBinding.Revision,
            binding.ExecutionBinding.ExecutionGeneration,
            activation.ActivationOrdinal,
            activation.VisitOrdinal,
            activation.NodeId,
            activation.Attempt,
            activation.CycleId,
            activation.CycleIteration,
            GovernedLoopControlCondition.Failure,
            activation.SelectedControlEdgeIds,
            activation.SkippedControlEdgeIds,
            null,
            null,
            CustomLoopSequentialNodeDisposition.Rejected,
            CustomLoopSequentialOutcomeArtifactHash.Compute(runEvent),
            string.Empty)
        {
            FailureEvidenceId = failure.EvidenceId,
            FailureEvidenceHash = failure.ContentHash,
        };
        evidence = CustomLoopSequentialNodeEvidenceHash.Apply(evidence);
        return runEvent with { SequentialNodeEvidence = evidence };
    }

    private static CustomLoopRunEvent AttachSequentialReviewEvidence(
        CustomLoopRunEvent runEvent,
        GovernedLoopSequentialAdapterBinding binding,
        GovernedLoopNodeExecutionEvidence activation,
        GovernedLoopFailureEvidence failure)
    {
        var evidence = new CustomLoopSequentialNodeEvidence(
            CustomLoopSequentialNodeEvidence.CurrentSchemaVersion,
            CustomLoopSequentialNodeEvidenceKind.AmbiguityAttention,
            binding.WorkspaceId,
            binding.ExecutionBinding.RunId,
            binding.ExecutionBinding.Revision,
            binding.ExecutionBinding.ExecutionGeneration,
            activation.ActivationOrdinal,
            activation.VisitOrdinal,
            activation.NodeId,
            activation.Attempt,
            activation.CycleId,
            activation.CycleIteration,
            null,
            [],
            [],
            null,
            null,
            CustomLoopSequentialNodeDisposition.NeedsReview,
            CustomLoopSequentialOutcomeArtifactHash.Compute(runEvent),
            string.Empty)
        {
            FailureEvidenceId = failure.EvidenceId,
            FailureEvidenceHash = failure.ContentHash,
        };
        evidence = CustomLoopSequentialNodeEvidenceHash.Apply(evidence);
        return runEvent with { SequentialNodeEvidence = evidence };
    }

    private static CustomLoopRunEvent LifecycleEvent(
        CustomLoopRunRecord run,
        DateTimeOffset terminalizedAtUtc,
        bool acceptedResponse,
        string terminalEventId,
        int sequence)
        => new(
            sequence,
            (acceptedResponse ? "human-input-response-frontier-" : "human-input-no-response-frontier-") + EventIdFragment(terminalEventId),
            terminalizedAtUtc,
            CustomLoopRunEventKind.LifecycleChanged,
            null,
            null,
            null,
            acceptedResponse
                ? "The exact accepted Human Input response advanced the canonical frontier before any later ordered re-entry."
                : "The exact Human Input no-response disposition advanced the canonical frontier without an accepted response.",
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

    private static string EventIdFragment(string eventId)
        => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(eventId)))[..24];

    private static IReadOnlyList<GovernedLoopHumanInputWaitingCheckpoint> ReplaceCheckpoint(
        IReadOnlyList<GovernedLoopHumanInputWaitingCheckpoint> checkpoints,
        GovernedLoopHumanInputWaitingCheckpoint replacement)
        => checkpoints.Select(item => string.Equals(item.Binding.CheckpointId, replacement.Binding.CheckpointId, StringComparison.Ordinal) ? replacement : item).ToArray();

    private static bool TryTerminalReceipt(
        GovernedLoopHumanInputWaitingCheckpoint checkpoint,
        GovernedLoopWakeContinuationRequest request,
        out string? evidenceHash)
    {
        evidenceHash = null;
        var terminal = checkpoint.Evidence.LastOrDefault();
        if (checkpoint.Posture != GovernedLoopHumanInputWaitingCheckpointPosture.Terminal
            || terminal?.Kind != GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Terminalized
            || !string.Equals(terminal.TerminalizationReceiptId, TerminalReceiptPrefix + request.ContinuationOperationId, StringComparison.Ordinal)
            || !string.Equals(terminal.TerminalizationReceiptHash, request.PreparedWakeEvidence?.ContentHash, StringComparison.Ordinal))
        {
            return false;
        }

        evidenceHash = terminal.EvidenceHash;
        return IsHash(evidenceHash);
    }

    private static bool TryExactNoResponseLifecycle(
        HumanInputResponseLifecycleStoreSnapshot snapshot,
        GovernedLoopHumanInputWaitingCheckpoint checkpoint,
        out NoResponseDisposition disposition,
        out DateTimeOffset retiredAtUtc)
    {
        disposition = NoResponseDisposition.Unknown;
        retiredAtUtc = default;
        var head = snapshot.Request?.Head;
        if (snapshot.Request is null
            || snapshot.ResponseRequest is null
            || !snapshot.ResponseRequest.Matches(checkpoint.Request)
            || head is null
            || !head.CurrentRequest.Matches(checkpoint.Request)
            || head.UpdatedAtUtc == default
            || head.UpdatedAtUtc.Offset != TimeSpan.Zero
            || head.UpdatedAtUtc < checkpoint.Evidence[^1].OccurredAtUtc
            || !snapshot.Request.RequestVersions.Any(item => head.CurrentRequest.Matches(item)))
        {
            return false;
        }

        retiredAtUtc = head.UpdatedAtUtc;
        disposition = head.Status switch
        {
            HumanInputRequestLifecycleStatus.Pending => NoResponseDisposition.Pending,
            HumanInputRequestLifecycleStatus.Expired when head.UpdatedAtUtc > checkpoint.Request.Timing.ExpiresAtUtc
                && HasExactLifecycleTerminal(snapshot.Request, head, checkpoint, HumanInputRequestLifecycleOperationKind.Expire) => NoResponseDisposition.Expired,
            HumanInputRequestLifecycleStatus.Cancelled when head.UpdatedAtUtc <= checkpoint.Request.Timing.ExpiresAtUtc
                && HasExactLifecycleTerminal(snapshot.Request, head, checkpoint, HumanInputRequestLifecycleOperationKind.Cancel) => NoResponseDisposition.Cancelled,
            HumanInputRequestLifecycleStatus.Rejected when head.UpdatedAtUtc <= checkpoint.Request.Timing.ExpiresAtUtc
                && HasExactLifecycleTerminal(snapshot.Request, head, checkpoint, HumanInputRequestLifecycleOperationKind.Reject) => NoResponseDisposition.Rejected,
            HumanInputRequestLifecycleStatus.Superseded when head.UpdatedAtUtc <= checkpoint.Request.Timing.ExpiresAtUtc
                && HasExactLifecycleTerminal(snapshot.Request, head, checkpoint, HumanInputRequestLifecycleOperationKind.Supersede) => NoResponseDisposition.SupersessionUnresolved,
            _ => NoResponseDisposition.Unknown,
        };
        return disposition != NoResponseDisposition.Unknown;
    }

    private static bool HasExactLifecycleTerminal(
        HumanInputRequestLifecycleStoreSnapshot snapshot,
        HumanInputRequestLifecycleHead head,
        GovernedLoopHumanInputWaitingCheckpoint checkpoint,
        HumanInputRequestLifecycleOperationKind kind)
    {
        var operation = snapshot.Operations?.LastOrDefault();
        return operation is not null
            && operation.Kind == kind
            && operation.Outcome == HumanInputRequestLifecycleOperationOutcome.Committed
            && string.Equals(operation.OperationId, head.LastOperationId, StringComparison.Ordinal)
            && string.Equals(operation.TargetRequestId, checkpoint.Request.RequestId, StringComparison.Ordinal)
            && operation.ExpectedRequest?.Matches(checkpoint.Request) == true
            && operation.PreviousHead is { Status: HumanInputRequestLifecycleStatus.Pending } previous
            && previous.CurrentRequest.Matches(checkpoint.Request)
            && Equals(operation.ResultHead, head)
            && operation.RecordedAtUtc == head.UpdatedAtUtc;
    }

    private static bool IsContinuationRequest(GovernedLoopWakeContinuationRequest? request, bool reconcileOnly)
        => request is not null
            && GovernedLoopSleepContractValidator.Validate(request.Checkpoint).IsValid
            && GovernedLoopSleepContractValidator.ValidateComposition(request.Checkpoint, request.Identity).IsValid
            && IsHash(request.ContinuationOperationId)
            && (reconcileOnly || request.PreparedWakeEvidence is not null
                && request.PreparedWakeEvidence.Disposition == GovernedLoopWakeDisposition.Prepared
                && GovernedLoopSleepContractValidator.ValidateComposition(request.Checkpoint, request.PreparedWakeEvidence).IsValid
                && string.Equals(request.PreparedWakeEvidence.Identity.ContentHash, request.Identity.ContentHash, StringComparison.Ordinal)
                && string.Equals(request.PreparedWakeEvidence.ContinuationOperationId, request.ContinuationOperationId, StringComparison.Ordinal)
                && IsHash(request.ExpectedPostureHash));

    private static bool TryParseEventReference(string? value, out string checkpointId)
    {
        checkpointId = string.Empty;
        return value is not null
            && value.StartsWith(GovernedLoopHumanInputContinuationVocabulary.AuthenticatedEventReferencePrefix, StringComparison.Ordinal)
            && (checkpointId = value[GovernedLoopHumanInputContinuationVocabulary.AuthenticatedEventReferencePrefix.Length..]).Length > 0
            && checkpointId.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.');
    }

    private bool TryNow(out DateTimeOffset utcNow)
    {
        try
        {
            utcNow = _timeProvider.GetUtcNow();
            return utcNow != default && utcNow.Offset == TimeSpan.Zero;
        }
        catch
        {
            utcNow = default;
            return false;
        }
    }

    private bool TryNow(DateTimeOffset lowerBound, DateTimeOffset evidenceBound, out DateTimeOffset utcNow)
        => TryNow(out utcNow) && utcNow >= lowerBound && utcNow >= evidenceBound;

    private static bool IsCandidate(HumanInputResponseContinuationCandidate? candidate)
        => candidate is not null
            && CustomLoopArtifactIdentifier.IsValid(candidate.RunId)
            && EmbodySense.Core.Common.HumanInput.HumanInputIdentifier.IsValid(candidate.CheckpointId);

    private static bool IsHash(string? value)
        => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static GovernedLoopAuthenticatedWakeVerificationResult Verification(GovernedLoopAuthenticatedWakeVerificationStatus status)
        => new(status);

    private static GovernedLoopWakeContinuationResult Continuation(GovernedLoopWakeContinuationStatus status, string? reference = null)
        => status == GovernedLoopWakeContinuationStatus.Committed
            ? new GovernedLoopWakeContinuationResult(status, reference, null)
            : new GovernedLoopWakeContinuationResult(status, null, reference);

    private static HumanInputResponseContinuationWakeResult Wake(HumanInputResponseContinuationWakeStatus status, GovernedLoopWakeResult? wake = null)
        => new(status, wake);

    private static HumanInputResponseContinuationWakeStatus Map(GovernedLoopSleepPublicationStatus status)
        => status is GovernedLoopSleepPublicationStatus.Invalid or GovernedLoopSleepPublicationStatus.Conflict
            ? HumanInputResponseContinuationWakeStatus.Invalid
            : status is GovernedLoopSleepPublicationStatus.Unavailable or GovernedLoopSleepPublicationStatus.Ambiguous
                ? HumanInputResponseContinuationWakeStatus.Unavailable
                : HumanInputResponseContinuationWakeStatus.Stale;

    private static HumanInputResponseContinuationWakeStatus Map(HumanInputResponseContinuationRunReadStatus status)
        => status switch
        {
            HumanInputResponseContinuationRunReadStatus.Invalid => HumanInputResponseContinuationWakeStatus.Invalid,
            HumanInputResponseContinuationRunReadStatus.Unavailable => HumanInputResponseContinuationWakeStatus.Unavailable,
            _ => HumanInputResponseContinuationWakeStatus.Stale,
        };

    private static HumanInputResponseContinuationWakeStatus Map(SelectionAttachmentStatus status)
        => status switch
        {
            SelectionAttachmentStatus.Invalid => HumanInputResponseContinuationWakeStatus.Invalid,
            SelectionAttachmentStatus.Unavailable => HumanInputResponseContinuationWakeStatus.Unavailable,
            SelectionAttachmentStatus.Retired => HumanInputResponseContinuationWakeStatus.Retired,
            SelectionAttachmentStatus.NoWork => HumanInputResponseContinuationWakeStatus.NoWork,
            _ => HumanInputResponseContinuationWakeStatus.Stale,
        };

}
