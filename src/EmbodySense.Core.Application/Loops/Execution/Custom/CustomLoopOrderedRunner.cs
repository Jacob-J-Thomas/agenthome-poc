using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

/// <summary>
/// Executes admitted custom-loop steps in order with durable checkpoints, bounded attempts, authority revalidation, and fail-closed recovery evidence.
/// </summary>
/// <remarks>
/// Provider dispatch occurs only after admission integrity, lifecycle, the run store's pre-dispatch hook, and current
/// tool-authority checks. Trace-capacity and lifecycle revalidation at that boundary depend on the store overriding the
/// compatibility hook, whose default only observes cancellation. A provider outcome is never silently retried when
/// persistence is uncertain; durable trace evidence instead stops the run for review.
/// </remarks>
public sealed class CustomLoopOrderedRunner : ICustomLoopResumeExecutor, ICustomLoopExecutionCancellationSignal
{
    private static readonly TimeSpan _integrityWriteTimeout = TimeSpan.FromSeconds(30);
    private const string SequentialConversationTurnCapabilityId = "org.embodysense/conversation-turn";
    private const string SequentialModelInferenceCapabilityId = "org.embodysense/model-inference";
    private const string SequentialWorkspaceCommandCapabilityId = "org.embodysense/workspace-command";
    private const string PublicationPublishedDetail = "Canonical output was published to the invoking conversation.";
    private const string PublicationAlreadyPublishedDetail = "Idempotent conversation publication was already committed.";
    private const string PublicationDefinitelyFailedDetail = "Conversation publication definitely failed; no success is reported.";
    private const string PublicationUncertainDetail = "Conversation publication outcome is uncertain and requires review.";
    private const string PublicationMismatchedIdentityDetail = "Conversation publisher returned an operation ID that did not match the durable publication intent.";
    private const string PublicationUnsupportedDetail = "Conversation publisher returned an unsupported outcome that requires review.";
    private const string PublicationOmittedDetail = "Conversation publication was selected but omitted because admission bound no invoking conversation.";
    private const string CanonicalCallerCancellationDetail = "Caller cancellation rejected the canonical node before provider invocation.";
    private const string CanonicalDurableCancellationDetail = "Durable cancellation rejected the canonical node before provider invocation.";
    private const string CanonicalPauseRejectionDetail = "A durable pause request rejected this canonical attempt before provider invocation; Resume may dispatch the next canonical attempt.";
    private const string CanonicalDeadlineRejectionDetail = "The custom-loop execution deadline was reached before the provider request could start.";
    private const string CanonicalPreProviderRejectionStartDetail = "Canonical node dispatch was retained before its pre-provider checks were rejected.";

    private readonly ICustomLoopRunStore _runStore;
    private readonly CustomLoopContextResolver _contextResolver;
    private readonly ICustomLoopInferenceAttemptExecutor _inferenceExecutor;
    private readonly ICustomLoopConversationPublisher _conversationPublisher;
    private readonly IAuditLog _auditLog;
    private readonly ICustomLoopToolAuthorityProvider _authorityProvider;
    private readonly ICustomLoopAttemptCancellationBroker? _attemptCancellationBroker;
    private readonly ICapabilityAdmissionService? _capabilityAdmissionService;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, byte> _activeRuns = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeAttemptCancellations = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="CustomLoopOrderedRunner"/> type.
    /// </summary>
    /// <param name="runStore">The run store.</param>
    /// <param name="contextResolver">The context resolver.</param>
    /// <param name="inferenceExecutor">The inference executor.</param>
    /// <param name="conversationPublisher">The conversation publisher.</param>
    /// <param name="auditLog">The audit log.</param>
    /// <param name="authorityProvider">The authority provider.</param>
    /// <param name="timeProvider">The time provider.</param>
    /// <param name="attemptCancellationBroker">The attempt cancellation broker.</param>
    /// <param name="capabilityAdmissionService">The current exact capability and narrower-authority revalidator.</param>
    public CustomLoopOrderedRunner(
        ICustomLoopRunStore runStore,
        CustomLoopContextResolver contextResolver,
        ICustomLoopInferenceAttemptExecutor inferenceExecutor,
        ICustomLoopConversationPublisher conversationPublisher,
        IAuditLog auditLog,
        ICustomLoopToolAuthorityProvider authorityProvider,
        TimeProvider? timeProvider = null,
        ICustomLoopAttemptCancellationBroker? attemptCancellationBroker = null,
        ICapabilityAdmissionService? capabilityAdmissionService = null)
    {
        _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
        _contextResolver = contextResolver ?? throw new ArgumentNullException(nameof(contextResolver));
        _inferenceExecutor = inferenceExecutor ?? throw new ArgumentNullException(nameof(inferenceExecutor));
        _conversationPublisher = conversationPublisher ?? throw new ArgumentNullException(nameof(conversationPublisher));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _authorityProvider = authorityProvider ?? throw new ArgumentNullException(nameof(authorityProvider));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _attemptCancellationBroker = attemptCancellationBroker;
        _capabilityAdmissionService = capabilityAdmissionService;
    }

    /// <summary>
    /// Starts public execution from the durable <c>Admitted</c> state only.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The terminal, paused, cancelled, failed, or invalid-state execution result.</returns>
    public Task<CustomLoopOrderedRunResult> RunAsync(CustomLoopOrderedRunRequest request, CancellationToken cancellationToken = default)
        => RunCoreAsync(request, null, cancellationToken);

    internal Task<CustomLoopOrderedRunResult> RunSequentialAsync(
        GovernedLoopSequentialOrderedRunRequest request,
        IGovernedLoopSequentialOrderedNodeEvidenceRecorder nodeEvidenceRecorder,
        IGovernedLoopSequentialAuditRecorder auditRecorder,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(nodeEvidenceRecorder);
        ArgumentNullException.ThrowIfNull(auditRecorder);
        var context = CreateSequentialContext(
            request.SchemaVersion,
            request.Anchor,
            request.Plan,
            request.Artifact,
            nodeEvidenceRecorder,
            auditRecorder);
        if (context is null)
        {
            return Task.FromResult(Result(CustomLoopOrderedRunStatus.InvalidState, null, "The canonical sequential hand-off is invalid and no ordered runtime work was dispatched."));
        }

        var runId = context.Anchor.AdapterBinding.ExecutionBinding.RunId;
        return RunCoreAsync(new CustomLoopOrderedRunRequest(runId, request.Actor), context, cancellationToken);
    }

    private async Task<CustomLoopOrderedRunResult> RunCoreAsync(
        CustomLoopOrderedRunRequest request,
        SequentialExecutionContext? sequentialContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Actor);

        CustomLoopRunRecord? run;
        try
        {
            using var integrity = new CancellationTokenSource(_integrityWriteTimeout);
            run = await _runStore.GetAsync(request.RunId, integrity.Token);
        }
        catch (Exception exception)
        {
            return Result(CustomLoopOrderedRunStatus.Failed, null, $"The run trace could not be loaded safely: {SafeExceptionClass(exception)}.");
        }

        if (run is null)
        {
            return Result(CustomLoopOrderedRunStatus.NotFound, null, "The custom-loop run does not exist.");
        }

        if (sequentialContext is not null && !SequentialRunMatches(run, sequentialContext))
        {
            return Result(CustomLoopOrderedRunStatus.InvalidState, run, "The durable ordered run does not match the exact canonical graph, admission, invocation, and node identities; no provider request was dispatched.");
        }

        if (run.Status == CustomLoopRunStatus.Admitted && cancellationToken.IsCancellationRequested)
        {
            return await CancelBeforeDispatchAsync(run, request.Actor);
        }

        string? sequentialCapabilityFailure = null;
        if (!run.IsTerminal)
        {
            string? capabilityFailure;
            try
            {
                capabilityFailure = await GetCapabilityFailureAsync(run, cancellationToken, sequentialContext?.AllowedCapabilityIds);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                capabilityFailure = $"Custom-loop capability revalidation could not complete safely: {SafeExceptionClass(exception)}.";
            }

            if (capabilityFailure is not null)
            {
                if (sequentialContext is null)
                {
                    return Result(CustomLoopOrderedRunStatus.InvalidState, run, capabilityFailure);
                }

                sequentialCapabilityFailure = capabilityFailure;
            }
        }

        var validation = CustomLoopRunValidator.ValidateForDispatch(run);
        if (!validation.IsValid)
        {
            var detail = validation.Errors.Any(error => string.Equals(error.Code, "admission_audit_incomplete", StringComparison.Ordinal))
                ? "The persisted custom-loop admission has no durable audit-completion marker and no provider request was dispatched."
                : "The persisted custom-loop run is invalid and no provider request was dispatched.";
            return Result(CustomLoopOrderedRunStatus.InvalidState, run, detail);
        }

        if (run.Status == CustomLoopRunStatus.Admitted)
        {
            using var ownership = TryRegisterActiveRun(run.Id);
            if (ownership is null)
            {
                return Result(CustomLoopOrderedRunStatus.Failed, run, "This runtime is already coordinating ordered execution for the custom-loop run.");
            }

            var started = sequentialContext is null
                ? await StartRunAsync(run, request.Actor, cancellationToken)
                : await DispatchSequentialNodeAsync(
                    sequentialContext,
                    sequentialContext.Plan.Nodes[0],
                    1,
                    request.Actor,
                    token => StartRunAsync(run, request.Actor, token),
                    cancellationToken);
            if (started.Terminal is not null)
            {
                return started.Terminal;
            }

            if (sequentialCapabilityFailure is not null)
            {
                var inferenceNode = sequentialContext!.Plan.Nodes[1];
                var rejected = await DispatchSequentialNodeAsync(
                    sequentialContext,
                    inferenceNode,
                    1,
                    request.Actor,
                    token => RejectSequentialNodeBeforeProviderAsync(
                        started.Run!,
                        request.Actor,
                        new SequentialNodeExecutionContext(
                            sequentialContext.Anchor.AdapterBinding,
                            sequentialContext.Artifact,
                            inferenceNode,
                            1,
                            sequentialContext.AllowedCapabilityIds,
                            sequentialContext.AuditRecorder),
                        inferenceNode.NodeId,
                        isExit: false,
                        "canonical_run_capability_invalid",
                        sequentialCapabilityFailure),
                    cancellationToken);
                return rejected.Terminal
                    ?? Result(CustomLoopOrderedRunStatus.NeedsReview, rejected.Run, "Canonical run-start capability rejection did not produce a closed terminal disposition.");
            }

            return await ContinueRegisteredAsync(started.Run!, request.Actor, cancellationToken, sequentialContext);
        }

        return Result(CustomLoopOrderedRunStatus.InvalidState, run, "Public execution starts only from Admitted. Interrupted runs require explicit recovery to Paused and a separate authenticated Resume path.");
    }

    /// <summary>
    /// Continues a run after an authenticated, durably recorded paused-to-running transition.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The continued execution result, or a fail-closed ownership/state result.</returns>
    public Task<CustomLoopOrderedRunResult> ResumeAsync(CustomLoopResumeExecutionRequest request, CancellationToken cancellationToken = default)
        => ResumeCoreAsync(request, null, cancellationToken);

    internal Task<CustomLoopOrderedRunResult> ResumeSequentialAsync(
        GovernedLoopSequentialOrderedResumeRequest request,
        IGovernedLoopSequentialOrderedNodeEvidenceRecorder nodeEvidenceRecorder,
        IGovernedLoopSequentialAuditRecorder auditRecorder,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(nodeEvidenceRecorder);
        ArgumentNullException.ThrowIfNull(auditRecorder);
        var context = CreateSequentialContext(
            request.SchemaVersion,
            request.Anchor,
            request.Plan,
            request.Artifact,
            nodeEvidenceRecorder,
            auditRecorder);
        if (context is null)
        {
            return Task.FromResult(Result(CustomLoopOrderedRunStatus.InvalidState, null, "The canonical sequential resume hand-off is invalid and no ordered runtime work was dispatched."));
        }

        var resume = new CustomLoopResumeExecutionRequest(
            context.Anchor.AdapterBinding.ExecutionBinding.RunId,
            request.RunningLifecycleVersion,
            request.ResumeOperationId,
            request.Actor,
            request.ActiveRunAlreadyRegistered);
        return ResumeCoreAsync(resume, context, cancellationToken);
    }

    private async Task<CustomLoopOrderedRunResult> ResumeCoreAsync(
        CustomLoopResumeExecutionRequest request,
        SequentialExecutionContext? sequentialContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        CustomLoopArtifactIdentifier.Require(request.RunId, nameof(request.RunId));
        CustomLoopArtifactIdentifier.Require(request.ResumeOperationId, nameof(request.ResumeOperationId), CustomLoopLimits.MaxMutationOperationIdCharacters);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Actor);
        if (request.RunningLifecycleVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Running lifecycle version must be at least one.");
        }

        CustomLoopRunRecord? run;
        try
        {
            run = await _runStore.GetAsync(request.RunId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Result(CustomLoopOrderedRunStatus.Failed, null, $"The resumed run trace could not be loaded safely: {SafeExceptionClass(exception)}.");
        }

        if (run is null)
        {
            return Result(CustomLoopOrderedRunStatus.NotFound, null, "The custom-loop run does not exist.");
        }

        if (sequentialContext is not null && !SequentialRunMatches(run, sequentialContext))
        {
            return Result(CustomLoopOrderedRunStatus.InvalidState, run, "The durable ordered run no longer matches the original canonical graph, admission, invocation, and node identities; no provider request was dispatched.");
        }

        var validation = CustomLoopRunValidator.ValidateForDispatch(run);
        if (!validation.IsValid)
        {
            var detail = validation.Errors.Any(error => string.Equals(error.Code, "admission_audit_incomplete", StringComparison.Ordinal))
                ? "The persisted custom-loop admission has no durable audit-completion marker and no provider request was dispatched."
                : "The persisted custom-loop run is invalid and no provider request was dispatched.";
            return Result(CustomLoopOrderedRunStatus.InvalidState, run, detail);
        }

        var matchingResume = run.Events.LastOrDefault() is { Kind: CustomLoopRunEventKind.LifecycleChanged } lifecycle && string.Equals(lifecycle.EventId, request.ResumeOperationId, StringComparison.Ordinal);
        if (run.Status != CustomLoopRunStatus.Running || run.LifecycleVersion != request.RunningLifecycleVersion || !matchingResume)
        {
            return Result(CustomLoopOrderedRunStatus.InvalidState, run, "Internal Resume requires the exact Paused-to-Running lifecycle version and matching durable operation event; public RunAsync cannot resume Running state.");
        }

        if (request.ActiveRunAlreadyRegistered)
        {
            return _activeRuns.ContainsKey(run.Id)
                ? await ContinueRegisteredAsync(run, request.Actor, cancellationToken, sequentialContext)
                : Result(CustomLoopOrderedRunStatus.Failed, run, "The resumed run was not registered as locally owned before its Running transition.");
        }

        using var ownership = TryRegisterActiveRun(run.Id);
        return ownership is null
            ? Result(CustomLoopOrderedRunStatus.Failed, run, "This runtime is already coordinating ordered execution for the custom-loop run.")
            : await ContinueRegisteredAsync(run, request.Actor, cancellationToken, sequentialContext);
    }

    /// <summary>
    /// Attempts to claim in-process ownership for one active run.
    /// </summary>
    /// <param name="runId">The run ID.</param>
    /// <returns>A registration lease, or <see langword="null"/> when this runtime already owns the run.</returns>
    public IDisposable? TryRegisterActiveRun(string runId)
    {
        if (!CustomLoopArtifactIdentifier.IsValid(runId) || !_activeRuns.TryAdd(runId, 0))
        {
            return null;
        }

        return new ActiveRunRegistration(_activeRuns, runId);
    }

    /// <summary>
    /// Signals cancellation to the provider attempt currently owned by this runtime.
    /// </summary>
    /// <param name="runId">The run ID.</param>
    public void CancelActiveAttempt(string runId)
    {
        if (!CustomLoopArtifactIdentifier.IsValid(runId))
        {
            return;
        }

        if (_activeAttemptCancellations.TryGetValue(runId, out var source))
        {
            try
            {
                source.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The provider attempt completed concurrently with the durable cancellation request.
            }

            return;
        }

        if (_activeRuns.ContainsKey(runId))
        {
            return;
        }

        throw new InvalidOperationException("The active provider attempt is not owned by this runtime and could not be signalled locally.");
    }

    /// <summary>
    /// Requests idempotent cancellation of the active provider attempt.
    /// </summary>
    /// <param name="runId">The run ID.</param>
    /// <param name="operationId">The operation ID.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>Whether the signal was delivered, no attempt was active, or its owner was unavailable.</returns>
    public Task<CustomLoopAttemptCancellationResult> RequestActiveAttemptCancellationAsync(string runId, string operationId, CancellationToken cancellationToken = default)
    {
        CustomLoopArtifactIdentifier.Require(runId, nameof(runId));
        CustomLoopArtifactIdentifier.Require(operationId, nameof(operationId), CustomLoopLimits.MaxMutationOperationIdCharacters);
        if (_attemptCancellationBroker is not null)
        {
            return _attemptCancellationBroker.RequestCancellationAsync(runId, operationId, cancellationToken);
        }

        try
        {
            CancelActiveAttempt(runId);
            var status = _activeAttemptCancellations.ContainsKey(runId)
                ? CustomLoopAttemptCancellationStatus.SignalDelivered
                : CustomLoopAttemptCancellationStatus.NoActiveAttempt;
            var detail = status == CustomLoopAttemptCancellationStatus.SignalDelivered
                ? "The active provider attempt cancellation token was signalled in this runtime."
                : "This runtime owns the run, but no provider attempt was active at the cancellation boundary.";
            return Task.FromResult(new CustomLoopAttemptCancellationResult(status, detail));
        }
        catch (Exception exception)
        {
            return Task.FromResult(new CustomLoopAttemptCancellationResult(CustomLoopAttemptCancellationStatus.OwnerUnavailable, $"The active provider attempt owner could not be reached: {SafeExceptionClass(exception)}."));
        }
    }

    private async Task<CustomLoopOrderedRunResult> ContinueRegisteredAsync(
        CustomLoopRunRecord run,
        string actor,
        CancellationToken cancellationToken,
        SequentialExecutionContext? sequentialContext = null)
    {
        var dispatchState = new ProviderDispatchState();
        var result = await ContinueAsync(run, actor, dispatchState, cancellationToken, sequentialContext);
        return result with { ProviderWasInvoked = dispatchState.ProviderWasInvoked };
    }

    private async Task<CustomLoopOrderedRunResult> ContinueAsync(
        CustomLoopRunRecord run,
        string actor,
        ProviderDispatchState dispatchState,
        CancellationToken cancellationToken,
        SequentialExecutionContext? sequentialContext)
    {
        while (true)
        {
            var boundary = await ObserveControlBoundaryAsync(run, actor);
            if (boundary.Terminal is not null)
            {
                return boundary.Terminal;
            }

            run = boundary.Run!;
            if (cancellationToken.IsCancellationRequested)
            {
                return await CancelBeforeDispatchAsync(run, actor);
            }

            if (GetAccumulatedRunningMilliseconds(run, Now(run)) >= CustomLoopLimits.MaxRunExecutionMilliseconds)
            {
                return await TerminateAsync(run, actor, CustomLoopRunStatus.Failed, "run_deadline_exceeded", "The custom-loop execution deadline was reached before another provider request could start.");
            }

            if (run.Checkpoint.NextStepIndex < run.AdmittedDefinition.InferenceSteps.Length)
            {
                var step = run.AdmittedDefinition.InferenceSteps[run.Checkpoint.NextStepIndex];
                var advanced = sequentialContext is null
                    ? await ExecuteInferenceStepAsync(run, step, actor, dispatchState, cancellationToken)
                    : await DispatchAndAdvanceSequentialInferenceAsync(
                        sequentialContext,
                        run,
                        step,
                        actor,
                        dispatchState,
                        cancellationToken);
                if (advanced.Terminal is not null)
                {
                    return advanced.Terminal;
                }

                run = advanced.Run!;
                continue;
            }

            if (HasCommittedExitCompletion(run))
            {
                return await TerminateAsync(run, actor, CustomLoopRunStatus.Completed, null, "The previously committed Exit decision completed the loop without another provider dispatch.", run.Checkpoint.CurrentIterationResult!.Content);
            }

            var exit = run.AdmittedDefinition.ExitPolicy;
            if (exit.MaxAdditionalIterations == 0)
            {
                const string Detail = "Continuation is disabled; Exit completed without a model call.";
                return sequentialContext is null
                    ? await CompleteDeterministicallyAsync(run, actor, Detail, cancellationToken)
                    : await DispatchAndAdvanceSequentialExitAsync(
                        sequentialContext,
                        run,
                        actor,
                        Detail,
                        cancellationToken);
            }

            if (run.Checkpoint.AcceptedRepeatCount >= exit.MaxAdditionalIterations)
            {
                return await CompleteDeterministicallyAsync(run, actor, "The repeat ceiling was reached; Exit completed without a model call.", cancellationToken);
            }

            var exitAdvance = await ExecuteExitAsync(run, actor, dispatchState, cancellationToken);
            if (exitAdvance.Terminal is not null)
            {
                return exitAdvance.Terminal;
            }

            run = exitAdvance.Run!;
        }
    }

    private async Task<RunAdvance> DispatchAndAdvanceSequentialInferenceAsync(
        SequentialExecutionContext context,
        CustomLoopRunRecord run,
        CustomLoopInferenceStep step,
        string actor,
        ProviderDispatchState dispatchState,
        CancellationToken cancellationToken)
    {
        var node = context.Plan.Nodes[run.Checkpoint.NextStepIndex + 1];
        var attempt = SequentialDispatchAttempt(run, node);
        var prepared = await DispatchSequentialNodeAsync(
            context,
            node,
            attempt,
            actor,
            token => PrepareOrExecuteSequentialInferenceAsync(context, node, attempt, run, step, actor, dispatchState, token),
            cancellationToken);
        if (prepared.Terminal is not null)
        {
            return prepared;
        }

        if (prepared.PendingCheckpoint is null)
        {
            var terminal = await TerminateAsync(prepared.Run!, actor, CustomLoopRunStatus.NeedsReview, "canonical_checkpoint_missing", "Canonical inference evidence resolved, but the ordered handler returned no checkpoint advancement.");
            return new RunAdvance(terminal.Run, terminal);
        }

        return await CommitCheckpointAsync(prepared.Run!, prepared.PendingCheckpoint, $"Inference checkpoint committed after `{step.Id}`.");
    }

    private async Task<RunAdvance> PrepareOrExecuteSequentialInferenceAsync(
        SequentialExecutionContext context,
        GovernedLoopSequentialPlanNode node,
        int attempt,
        CustomLoopRunRecord run,
        CustomLoopInferenceStep step,
        string actor,
        ProviderDispatchState dispatchState,
        CancellationToken cancellationToken)
    {
        var iteration = run.Checkpoint.Iteration;
        var completed = run.Events.LastOrDefault(item => item.Kind == CustomLoopRunEventKind.NodeAttemptCompleted
            && item.Iteration == iteration
            && string.Equals(item.StepId, step.Id, StringComparison.Ordinal)
            && item.Attempt == attempt
            && item.SequentialNodeEvidence is
            {
                Kind: CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
                Disposition: CustomLoopSequentialNodeDisposition.Completed,
            } completion
            && completion.Attempt == attempt
            && string.Equals(completion.NodeId, node.NodeId, StringComparison.Ordinal));
        if (completed is not null)
        {
            var observed = run.Events.LastOrDefault(item => item.Sequence < completed.Sequence
                && item.Kind == CustomLoopRunEventKind.NodeOutcomeObserved
                && item.Iteration == iteration
                && string.Equals(item.StepId, step.Id, StringComparison.Ordinal)
                && item.Attempt == attempt);
            var reservations = run.Events.Count(item => item.Sequence < completed.Sequence
                && item.Kind == CustomLoopRunEventKind.ToolRequestReserved
                && item.Iteration == iteration
                && string.Equals(item.StepId, step.Id, StringComparison.Ordinal)
                && item.Attempt == attempt);
            if (observed?.CanonicalOutput is null
                || completed.CanonicalOutput is null
                || !string.Equals(observed.CanonicalOutput, completed.CanonicalOutput, StringComparison.Ordinal))
            {
                var invalid = await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, "canonical_outcome_reconciliation_failed", "The retained ordered inference outcome is incomplete or divergent; automatic provider redispatch is forbidden.");
                return new RunAdvance(invalid.Run, invalid);
            }

            var replayResult = new CustomLoopInferenceAttemptResult(
                completed.CanonicalOutput,
                completed.Provider ?? string.Empty,
                completed.Model,
                completed.ProviderResponseId,
                reservations);
            var integrityError = ValidateProviderResult(run, replayResult, iteration, step.Id, attempt, out var durableToolRequestsConsumed);
            if (integrityError is not null)
            {
                var invalid = await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, "canonical_outcome_reconciliation_failed", $"The retained ordered inference outcome could not be authenticated for advancement: {integrityError}");
                return new RunAdvance(invalid.Run, invalid);
            }

            var attemptStarted = run.Events.LastOrDefault(item => item.Sequence < completed.Sequence
                && item.Kind == CustomLoopRunEventKind.NodeAttemptStarted
                && item.Iteration == iteration
                && string.Equals(item.StepId, step.Id, StringComparison.Ordinal)
                && item.Attempt == attempt);
            CustomLoopContextAssembly assembly;
            SequentialAuditBoundaryFailure? auditFailure;
            try
            {
                if (attemptStarted?.ToolAuthority is null)
                {
                    throw new InvalidOperationException("The retained attempt start has no immutable authority snapshot.");
                }

                EnsureAuthorityBound(run, attemptStarted.ToolAuthority, run.AdmittedDefinition.ToolAssignments);
                var effectiveAssignments = run.Checkpoint.ToolRequestsUsed < CustomLoopLimits.MaxModelVisibleGovernedToolRequestsPerRun
                    ? attemptStarted.ToolAuthority.EffectiveAssignments
                    : [];
                assembly = _contextResolver.ResolveInference(run, step, effectiveAssignments);
                EnsureRequestBound(assembly);
                if (attemptStarted.ProviderResponseId is null
                    || !attemptStarted.ContextBlocks.SequenceEqual(assembly.Blocks))
                {
                    throw new InvalidOperationException("The retained attempt start does not match the reconstructed inference request.");
                }

                var canonical = new CanonicalOutput(
                    completed.CanonicalOutput,
                    completed.OriginalOutputCharacterCount ?? completed.CanonicalOutput.Length,
                    completed.CanonicalOutputTruncated ?? false);
                auditFailure = await AppendOutcomeAuditAsync(
                    run,
                    completed,
                    AttemptAudit(
                        actor,
                        run,
                        step.Id,
                        iteration,
                        attemptStarted.ProviderResponseId,
                        assembly,
                        AuditSchema.Actions.LoopNodeAttempt,
                        AuditSchema.Outcomes.Succeeded,
                        canonical,
                        replayResult,
                        attempt: attempt),
                    context.AuditRecorder,
                    IntegrityToken());
            }
            catch (Exception exception)
            {
                var invalid = await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, "attempt_outcome_audit_failed", $"The retained ordered inference outcome could not be re-audited before advancement: {SafeExceptionClass(exception)}.");
                return new RunAdvance(invalid.Run, invalid);
            }

            if (auditFailure is not null)
            {
                var invalid = await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, auditFailure.FailureCode, auditFailure.Detail);
                return new RunAdvance(invalid.Run, invalid);
            }

            var retained = new CustomLoopRetainedOutput(
                step.Id,
                iteration,
                completed.CanonicalOutput,
                CustomLoopTraceContentHash.Compute(completed.CanonicalOutput));
            var published = await PublishIfSelectedAsync(
                run,
                assembly.ResolvedOutputPolicy,
                retained,
                step.Id,
                isExit: false,
                actor,
                context.AllowedCapabilityIds);
            if (published.Terminal is not null)
            {
                return published;
            }

            run = published.Run!;
            var nextStepIndex = run.Checkpoint.NextStepIndex + 1;
            var earlier = completed.RetainedForLoopReasoning == true
                ? [.. run.Checkpoint.EarlierRetainedOutputs, retained]
                : run.Checkpoint.EarlierRetainedOutputs;
            var checkpoint = run.Checkpoint with
            {
                NextStepIndex = nextStepIndex,
                PendingExitDecision = nextStepIndex == run.AdmittedDefinition.InferenceSteps.Length
                    && run.AdmittedDefinition.ExitPolicy.MaxAdditionalIterations > run.Checkpoint.AcceptedRepeatCount,
                EarlierRetainedOutputs = earlier,
                CurrentIterationResult = retained,
                ToolRequestsUsed = checked(run.Checkpoint.ToolRequestsUsed + durableToolRequestsConsumed)
            };
            return new RunAdvance(run, null, checkpoint);
        }

        var rejected = run.Events.LastOrDefault(item => item.Kind == CustomLoopRunEventKind.NodeAttemptFailed
            && item.Iteration == iteration
            && string.Equals(item.StepId, step.Id, StringComparison.Ordinal)
            && item.Attempt == attempt
            && item.SequentialNodeEvidence is
            {
                Kind: CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection,
                Disposition: CustomLoopSequentialNodeDisposition.Rejected,
            } rejection
            && rejection.Attempt == attempt
            && string.Equals(rejection.NodeId, node.NodeId, StringComparison.Ordinal));
        if (rejected is not null)
        {
            return await ReconcileSequentialInferenceRejectionAsync(
                context,
                run,
                step,
                rejected,
                actor);
        }

        var started = run.Events.Any(item => item.Kind == CustomLoopRunEventKind.NodeAttemptStarted
            && item.Iteration == iteration
            && string.Equals(item.StepId, step.Id, StringComparison.Ordinal)
            && item.Attempt == attempt
            && item.SequentialNodeEvidence is
            {
                Kind: CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
                Disposition: CustomLoopSequentialNodeDisposition.Unknown,
            } dispatch
            && dispatch.Attempt == attempt
            && string.Equals(dispatch.NodeId, node.NodeId, StringComparison.Ordinal));
        if (started)
        {
            var ambiguous = await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, "canonical_open_attempt_requires_review", "The ordered inference attempt started without a complete retained outcome; automatic provider redispatch is forbidden.");
            return new RunAdvance(ambiguous.Run, ambiguous);
        }

        return await ExecuteInferenceStepAsync(
            run,
            step,
            actor,
            dispatchState,
            cancellationToken,
            deferCheckpoint: true,
            new SequentialNodeExecutionContext(context.Anchor.AdapterBinding, context.Artifact, node, attempt, context.AllowedCapabilityIds, context.AuditRecorder));
    }

    private async Task<CustomLoopOrderedRunResult> DispatchAndAdvanceSequentialExitAsync(
        SequentialExecutionContext context,
        CustomLoopRunRecord run,
        string actor,
        string detail,
        CancellationToken cancellationToken)
    {
        var node = context.Plan.Nodes[^1];
        var attempt = SequentialDispatchAttempt(run, node);
        var prepared = await DispatchSequentialNodeAsync(
            context,
            node,
            attempt,
            actor,
            token => PrepareOrExecuteSequentialExitAsync(context, node, attempt, run, actor, detail, token),
            cancellationToken);
        if (prepared.Terminal is not null)
        {
            return prepared.Terminal;
        }

        if (prepared.PendingCheckpoint is null || prepared.PendingTerminal is null)
        {
            return await TerminateAsync(prepared.Run!, actor, CustomLoopRunStatus.NeedsReview, "canonical_exit_advancement_missing", "Canonical Exit evidence resolved, but the ordered handler returned no terminal checkpoint advancement.");
        }

        return await CommitPreparedSequentialAdvancementAsync(prepared, actor, detail);
    }

    private async Task<RunAdvance> PrepareOrExecuteSequentialExitAsync(
        SequentialExecutionContext context,
        GovernedLoopSequentialPlanNode node,
        int attempt,
        CustomLoopRunRecord run,
        string actor,
        string detail,
        CancellationToken cancellationToken)
    {
        var iteration = run.Checkpoint.Iteration;
        var completed = run.Events.LastOrDefault(item => item.Kind == CustomLoopRunEventKind.ExitDecisionCompleted
            && item.Iteration == iteration
            && string.Equals(item.StepId, "exit", StringComparison.Ordinal)
            && item.Attempt == attempt
            && item.SequentialNodeEvidence is
            {
                Kind: CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
                Disposition: CustomLoopSequentialNodeDisposition.Completed,
            } completion
            && completion.Attempt == attempt
            && string.Equals(completion.NodeId, node.NodeId, StringComparison.Ordinal));
        if (completed is null)
        {
            var rejected = run.Events.LastOrDefault(item => item.Kind == CustomLoopRunEventKind.NodeAttemptFailed
                && item.Iteration == iteration
                && string.Equals(item.StepId, "exit", StringComparison.Ordinal)
                && item.Attempt == attempt
                && item.SequentialNodeEvidence is
                {
                    Kind: CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection,
                    Disposition: CustomLoopSequentialNodeDisposition.Rejected,
                } rejection
                && rejection.Attempt == attempt
                && string.Equals(rejection.NodeId, node.NodeId, StringComparison.Ordinal));
            if (rejected is not null)
            {
                return await ReconcileSequentialExitRejectionAsync(context, run, rejected, actor);
            }

            return await PrepareDeterministicExitAsync(
                run,
                actor,
                detail,
                cancellationToken,
                new SequentialNodeExecutionContext(context.Anchor.AdapterBinding, context.Artifact, node, attempt, context.AllowedCapabilityIds, context.AuditRecorder));
        }

        if (completed.ExitDecision != CustomLoopExitDecision.Complete || run.Checkpoint.CurrentIterationResult is null)
        {
            var invalid = await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, "canonical_exit_reconciliation_failed", "The retained ordered Exit outcome is incomplete or divergent; automatic terminal-effect redispatch is forbidden.");
            return new RunAdvance(invalid.Run, invalid);
        }

        CustomLoopContextOutputPolicy outputPolicy;
        try
        {
            outputPolicy = CustomLoopContextResolver.ResolvePolicy(
                run.AdmittedDefinition.ExitPolicy.ContextPolicy,
                run.AdmittedDefinition.ContextDefaults.Exit).ContextOut;
        }
        catch (Exception exception)
        {
            var invalid = await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, "canonical_exit_reconciliation_failed", $"The retained ordered Exit policy could not be authenticated: {SafeExceptionClass(exception)}.");
            return new RunAdvance(invalid.Run, invalid);
        }

        SequentialAuditBoundaryFailure? auditFailure;
        try
        {
            auditFailure = await AppendOutcomeAuditAsync(
                run,
                completed,
                CreateDeterministicExitAudit(run, actor, detail, completed),
                context.AuditRecorder,
                IntegrityToken());
        }
        catch (Exception exception)
        {
            var invalid = await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, "canonical_outcome_audit_unavailable", $"The retained ordered Exit outcome could not be re-audited before advancement: {SafeExceptionClass(exception)}.");
            return new RunAdvance(invalid.Run, invalid);
        }

        if (auditFailure is not null)
        {
            var invalid = await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, auditFailure.FailureCode, auditFailure.Detail);
            return new RunAdvance(invalid.Run, invalid);
        }

        var published = await PublishIfSelectedAsync(
            run,
            outputPolicy,
            run.Checkpoint.CurrentIterationResult,
            "exit",
            isExit: true,
            actor,
            context.AllowedCapabilityIds);
        if (published.Terminal is not null)
        {
            return published;
        }

        run = published.Run!;
        var checkpoint = run.Checkpoint with { PendingExitDecision = false };
        return new RunAdvance(
            run,
            null,
            checkpoint,
            new PendingTerminal(CustomLoopRunStatus.Completed, null, detail, run.Checkpoint.CurrentIterationResult!.Content));
    }

    private async Task<RunAdvance> DispatchSequentialNodeAsync(
        SequentialExecutionContext context,
        GovernedLoopSequentialPlanNode node,
        int attempt,
        string actor,
        Func<CancellationToken, Task<RunAdvance>> execute,
        CancellationToken cancellationToken)
    {
        var dispatchRequest = new GovernedLoopSequentialNodeDispatchRequest(
            GovernedLoopSequentialNodeDispatchRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            node,
            attempt);
        RunAdvance? advance = null;
        var disposition = GovernedLoopSequentialNodeHandlerResultStatus.Unknown;
        var handler = new SingleSequentialNodeHandler(
            node.Descriptor,
            async token =>
            {
                advance = await execute(token);
                var durableRun = advance.Run ?? advance.Terminal?.Run;
                var evidenceEvent = durableRun is null ? null : FindSequentialNodeEvidence(durableRun, node, attempt);
                disposition = SequentialDisposition(evidenceEvent?.SequentialNodeEvidence);
                if (durableRun is null || evidenceEvent is null)
                {
                    return new GovernedLoopSequentialNodeHandlerResult(GovernedLoopSequentialNodeHandlerResultStatus.Unknown, string.Empty);
                }

                try
                {
                    return await context.NodeEvidenceRecorder.RetainAsync(
                        new GovernedLoopSequentialOrderedNodeEvidenceRequest(
                            GovernedLoopSequentialOrderedNodeEvidenceRequest.CurrentSchemaVersion,
                            dispatchRequest,
                            disposition,
                            durableRun.LifecycleVersion,
                            evidenceEvent.Sequence,
                            evidenceEvent.EventId),
                        IntegrityToken());
                }
                catch
                {
                    return new GovernedLoopSequentialNodeHandlerResult(GovernedLoopSequentialNodeHandlerResultStatus.Unknown, string.Empty);
                }
            });
        var dispatcher = new GovernedLoopSequentialNodeDispatcher(
            new GovernedLoopSequentialNodeHandlerRegistry([handler]),
            context.NodeEvidenceRecorder);
        var dispatched = await dispatcher.DispatchAsync(dispatchRequest, cancellationToken);
        if (advance is not null && DispatchMatches(dispatched.Status, disposition))
        {
            return advance;
        }

        var current = advance?.Run ?? advance?.Terminal?.Run;
        if (current is null)
        {
            return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.InvalidState, null, "Canonical node dispatch was rejected before ordered runtime work began."));
        }

        var status = handler.WasInvoked ? CustomLoopRunStatus.NeedsReview : CustomLoopRunStatus.Failed;
        var failureCode = handler.WasInvoked ? "canonical_dispatch_evidence_invalid" : "canonical_dispatch_rejected";
        var orderedDetail = advance?.Terminal?.Detail;
        var detail = orderedDetail is null
            ? $"Canonical dispatch for node `{node.NodeId}` did not return exact retained evidence; automatic redispatch is forbidden."
            : $"Canonical dispatch for node `{node.NodeId}` did not return exact retained evidence after the ordered handler reported: {orderedDetail}";
        var terminal = await TerminateAsync(current, actor, status, failureCode, detail);
        return new RunAdvance(terminal.Run, terminal);
    }

    private async Task<RunAdvance> StartRunAsync(CustomLoopRunRecord run, string actor, CancellationToken cancellationToken)
    {
        var now = Now(run);
        var lifecycle = Event(run, now, CustomLoopRunEventKind.LifecycleChanged, "Run entered Running before ordered dispatch.");
        var candidate = run with
        {
            LifecycleVersion = run.LifecycleVersion + 1,
            Status = CustomLoopRunStatus.Running,
            UpdatedAtUtc = now,
            ExecutionClock = run.ExecutionClock with { ActiveSinceUtc = now },
            Events = [.. run.Events, lifecycle]
        };
        RunAdvance persisted;
        try
        {
            persisted = await PersistAsync(run, candidate, cancellationToken, outcomeMayExist: false, propagateCancellation: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var cancelled = await CancelAfterInterruptedPreDispatchPersistenceAsync(run, candidate, actor);
            return new RunAdvance(cancelled.Run, cancelled);
        }

        if (persisted.Terminal is not null)
        {
            return persisted;
        }

        run = persisted.Run!;
        var audit = AuditEvent.Create(actor, AuditSchema.Actions.LoopRunLifecycle, run.Id, AuditSchema.Outcomes.Started, "Custom-loop ordered execution entered Running.", RunMetadata(run));
        try
        {
            await _auditLog.AppendAsync(audit, cancellationToken);
            return new RunAdvance(run, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var cancelled = await CancelBeforeDispatchAsync(run, actor);
            return new RunAdvance(cancelled.Run, cancelled);
        }
        catch (Exception exception)
        {
            var terminal = await TerminateAsync(run, actor, CustomLoopRunStatus.Failed, "run_start_audit_failed", $"The run-start audit could not be recorded before dispatch: {SafeExceptionClass(exception)}.");
            return new RunAdvance(terminal.Run, terminal);
        }
    }

    private async Task<RunAdvance> ExecuteInferenceStepAsync(
        CustomLoopRunRecord run,
        CustomLoopInferenceStep step,
        string actor,
        ProviderDispatchState dispatchState,
        CancellationToken cancellationToken,
        bool deferCheckpoint = false,
        SequentialNodeExecutionContext? sequentialNode = null)
    {
        var attempt = sequentialNode?.Attempt ?? 1;
        CustomLoopContextAssembly assembly;
        CustomLoopToolAuthoritySnapshot authority;
        CustomLoopToolAssignment[] effectiveAssignments;
        try
        {
            var capabilityFailure = await GetCapabilityFailureAsync(run, cancellationToken, sequentialNode?.AllowedCapabilityIds);
            if (capabilityFailure is not null)
            {
                throw new InvalidOperationException(capabilityFailure);
            }

            authority = sequentialNode is not null && run.AdmittedDefinition.ToolAssignments.Length == 0
                ? CanonicalToolFreeAuthority(run)
                : await _authorityProvider.ResolveAsync(run.AdmittedDefinition.RoleId, run.AdmittedDefinition.ToolAssignments, cancellationToken);
            EnsureAuthorityBound(run, authority, run.AdmittedDefinition.ToolAssignments);

            effectiveAssignments = run.Checkpoint.ToolRequestsUsed < CustomLoopLimits.MaxModelVisibleGovernedToolRequestsPerRun ? authority.EffectiveAssignments : [];
            assembly = _contextResolver.ResolveInference(run, step, effectiveAssignments);
            EnsureRequestBound(assembly);
            EnsureAttemptBound(run);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (sequentialNode is not null)
            {
                return await RejectSequentialNodeBeforeProviderAsync(
                    run,
                    actor,
                    sequentialNode,
                    step.Id,
                    isExit: false,
                    failureCode: null,
                    CanonicalCallerCancellationDetail,
                    CustomLoopRunStatus.Cancelled);
            }

            var cancelled = await CancelBeforeDispatchAsync(run, actor);
            return new RunAdvance(cancelled.Run, cancelled);
        }
        catch (Exception exception)
        {
            if (sequentialNode is not null)
            {
                return await RejectSequentialNodeBeforeProviderAsync(
                    run,
                    actor,
                    sequentialNode,
                    step.Id,
                    isExit: false,
                    "invalid_inference_request",
                    $"The inference request could not be assembled safely: {SafeExceptionClass(exception)}.");
            }

            var terminal = await TerminateAsync(run, actor, CustomLoopRunStatus.Failed, "invalid_inference_request", $"The inference request could not be assembled safely: {SafeExceptionClass(exception)}.");
            return new RunAdvance(terminal.Run, terminal);
        }

        var iteration = run.Checkpoint.Iteration;
        var correlation = NewCorrelationId("attempt");
        var now = Now(run);
        var events = new List<CustomLoopRunEvent>();
        if (run.Checkpoint.NextStepIndex == 0)
        {
            events.Add(Event(run, now, CustomLoopRunEventKind.IterationStarted, $"Iteration {iteration} started in persisted step order.", iteration));
        }

        var sequenceOwner = events.Count == 0 ? run : run with { Events = [.. run.Events, .. events] };
        var attemptStarted = Event(sequenceOwner, now, CustomLoopRunEventKind.NodeAttemptStarted, "Inference attempt trace committed before provider dispatch.", iteration, step.Id, attempt, assembly.Blocks, provider: run.ModelSnapshot.Provider, model: run.ModelSnapshot.Model, providerResponseId: correlation, toolAuthority: authority, traceReservationUtf8Bytes: CustomLoopLimits.MaxAttemptEvidenceReservationUtf8Bytes);
        events.Add(sequentialNode is null
            ? attemptStarted
            : WithSequentialEvidence(attemptStarted, sequentialNode, CustomLoopSequentialNodeEvidenceKind.DispatchStarted, CustomLoopSequentialNodeDisposition.Unknown));
        var startedCandidate = Append(run, now, events);
        var capacityBoundary = await RejectUnavailableTraceCapacityAsync(run, startedCandidate, actor, "provider", cancellationToken);
        if (capacityBoundary is not null)
        {
            return capacityBoundary;
        }

        RunAdvance started;
        try
        {
            started = await PersistAsync(run, startedCandidate, cancellationToken, outcomeMayExist: false, propagateCancellation: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var cancelled = await CancelAfterInterruptedPreDispatchPersistenceAsync(run, startedCandidate, actor);
            return new RunAdvance(cancelled.Run, cancelled);
        }

        if (started.Terminal is not null)
        {
            return started;
        }

        run = started.Run!;
        try
        {
            await _auditLog.AppendAsync(AttemptAudit(actor, run, step.Id, iteration, correlation, assembly, AuditSchema.Actions.LoopNodeAttempt, AuditSchema.Outcomes.Started, null, null, attempt: attempt), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (sequentialNode is not null)
            {
                return await CloseSequentialAttemptAtControlBoundaryAsync(
                    run,
                    actor,
                    step.Id,
                    iteration,
                    correlation,
                    assembly,
                    sequentialNode,
                    CustomLoopRunStatus.Cancelled,
                    CanonicalCallerCancellationDetail);
            }

            var cancelled = await CancelBeforeDispatchAsync(run, actor);
            return new RunAdvance(cancelled.Run, cancelled);
        }
        catch (Exception exception)
        {
            if (sequentialNode is not null)
            {
                return await RecordAttemptFailureAsync(run, actor, step.Id, iteration, correlation, assembly, exception, isExit: false, providerWasInvoked: false, sequentialNode);
            }

            var terminal = await TerminateAsync(run, actor, CustomLoopRunStatus.Failed, "attempt_start_audit_failed", $"The attempt-start audit could not be recorded before dispatch: {SafeExceptionClass(exception)}.");
            return new RunAdvance(terminal.Run, terminal);
        }

        var assignments = Array.AsReadOnly(run.AdmittedDefinition.ToolAssignments.ToArray());
        var attemptRequest = new CustomLoopInferenceAttemptRequest(
            run.Id,
            run.LoopId,
            run.AdmittedDefinition.RoleId,
            run.AdmittedDefinition.DefinitionVersion,
            run.AdmittedDefinition.ContentHash,
            iteration,
            step.Id,
            attempt,
            correlation,
            IsExit: false,
            AllowTools: effectiveAssignments.Length > 0,
            run.ModelSnapshot,
            assignments,
            run.Checkpoint.ToolRequestsUsed,
            assembly.Request,
            authority)
        {
            CapabilityAdmission = sequentialNode?.Binding.AdmissionReceipt.Evidence.CapabilityAdmission ?? run.CapabilityAdmission,
            AdmissionReceipt = sequentialNode?.Binding.AdmissionReceipt,
            ExecutionBinding = sequentialNode?.Binding.ExecutionBinding,
            GraphArtifact = sequentialNode?.Artifact
        };

        CustomLoopInferenceAttemptResult result;
        // This flag is the effect boundary: cancellation before the callback is safe to report as
        // pre-dispatch, while any failure after it must assume the external provider may have acted.
        var providerInvoked = false;
        ICustomLoopAttemptCancellationRegistration? cancellationRegistration = null;
        using var providerBoundaryToken = CreateProviderToken(run, cancellationToken);
        using var providerToken = CancellationTokenSource.CreateLinkedTokenSource(providerBoundaryToken.Token);
        if (!_activeAttemptCancellations.TryAdd(run.Id, providerToken))
        {
            return await RecordAttemptFailureAsync(run, actor, step.Id, iteration, correlation, assembly, new InvalidOperationException("A provider attempt is already registered for this run."), isExit: false, providerWasInvoked: false, sequentialNode);
        }

        try
        {
            cancellationRegistration = _attemptCancellationBroker?.RegisterActiveAttempt(run.Id, providerToken, providerBoundaryToken.Token);
            var dispatchBoundary = sequentialNode is null
                ? await ObserveControlBoundaryAsync(run, actor)
                : await ObserveSequentialControlBoundaryAsync(
                    run,
                    actor,
                    step.Id,
                    iteration,
                    correlation,
                    assembly,
                    sequentialNode);
            if (dispatchBoundary.Terminal is not null)
            {
                return dispatchBoundary;
            }

            run = dispatchBoundary.Run!;
            if (ExecutionDeadlineReached(run))
            {
                if (sequentialNode is not null)
                {
                    return await RecordAttemptFailureAsync(
                        run,
                        actor,
                        step.Id,
                        iteration,
                        correlation,
                        assembly,
                        new TimeoutException("The canonical run deadline was reached before provider dispatch."),
                        isExit: false,
                        providerWasInvoked: false,
                        sequentialNode,
                        CustomLoopRunStatus.Failed,
                        "run_deadline_exceeded",
                        CanonicalDeadlineRejectionDetail);
                }

                var terminal = await TerminateAsync(run, actor, CustomLoopRunStatus.Failed, "run_deadline_exceeded", "The custom-loop execution deadline was reached before the provider request could start.");
                return new RunAdvance(terminal.Run, terminal);
            }

            providerToken.Token.ThrowIfCancellationRequested();
            result = await _inferenceExecutor.ExecuteAsync(attemptRequest, providerToken.Token, () =>
            {
                providerInvoked = true;
                dispatchState.MarkProviderRequestStarted();
            });
        }
        catch (OperationCanceledException exception) when (!providerInvoked)
        {
            if (sequentialNode is not null)
            {
                var callerCancelled = cancellationToken.IsCancellationRequested;
                var deadlineReached = !callerCancelled && ExecutionDeadlineReached(run);
                return await RecordAttemptFailureAsync(
                    run,
                    actor,
                    step.Id,
                    iteration,
                    correlation,
                    assembly,
                    exception,
                    isExit: false,
                    providerWasInvoked: false,
                    sequentialNode,
                    callerCancelled ? CustomLoopRunStatus.Cancelled : CustomLoopRunStatus.Failed,
                    callerCancelled ? null : deadlineReached ? "run_deadline_exceeded" : "provider_cancelled_before_dispatch",
                    callerCancelled
                        ? CanonicalCallerCancellationDetail
                        : deadlineReached
                            ? CanonicalDeadlineRejectionDetail
                            : "The provider request was cancelled before invocation without a matching caller, lifecycle, or deadline cancellation.");
            }

            return await HandlePreInvocationCancellationAsync(run, actor, cancellationToken);
        }
        catch (OperationCanceledException exception)
        {
            cancellationRegistration?.TryConfirmProviderInterruption(exception.CancellationToken);
            return await RecordAttemptFailureAsync(run, actor, step.Id, iteration, correlation, assembly, exception, isExit: false, providerWasInvoked: true, sequentialNode);
        }
        catch (Exception exception)
        {
            return await RecordAttemptFailureAsync(run, actor, step.Id, iteration, correlation, assembly, exception, isExit: false, providerWasInvoked: providerInvoked, sequentialNode);
        }
        finally
        {
            cancellationRegistration?.Dispose();
            _activeAttemptCancellations.TryRemove(run.Id, out _);
        }

        if (result is null)
        {
            return await RecordAttemptFailureAsync(run, actor, step.Id, iteration, correlation, assembly, new InvalidOperationException("Provider executor returned no result."), isExit: false, providerWasInvoked: providerInvoked, sequentialNode);
        }

        var refreshed = await RefreshControlUpdateAsync(run);
        if (refreshed.Terminal is not null)
        {
            return refreshed;
        }

        run = refreshed.Run!;

        var canonical = Canonicalize(result.OutputText);
        var retained = new CustomLoopRetainedOutput(step.Id, iteration, canonical.Text, CustomLoopTraceContentHash.Compute(canonical.Text));
        var publicationId = assembly.ResolvedOutputPolicy.PublishToInvokingConversation ? PublicationOperationId(run.Id, iteration, step.Id, isExit: false) : null;
        var observedNow = Now(run);
        var safeProviderResponseId = SafeReference(result.ProviderResponseId);
        var observed = Event(run, observedNow, CustomLoopRunEventKind.NodeOutcomeObserved, "Inference provider outcome was observed and retained as local evidence.", iteration, step.Id, attempt, output: canonical.Text, originalOutputCharacters: canonical.OriginalCharacterCount, truncated: canonical.Truncated, retained: assembly.ResolvedOutputPolicy.RetainForLoopReasoning, published: assembly.ResolvedOutputPolicy.PublishToInvokingConversation, publicationId: publicationId, provider: run.ModelSnapshot.Provider, model: run.ModelSnapshot.Model, providerResponseId: safeProviderResponseId);
        var completed = Event(run with { Events = [.. run.Events, observed] }, observedNow, CustomLoopRunEventKind.NodeAttemptCompleted, "Inference attempt completed without an automatic retry.", iteration, step.Id, attempt, output: canonical.Text, originalOutputCharacters: canonical.OriginalCharacterCount, truncated: canonical.Truncated, retained: assembly.ResolvedOutputPolicy.RetainForLoopReasoning, published: assembly.ResolvedOutputPolicy.PublishToInvokingConversation, publicationId: publicationId, provider: run.ModelSnapshot.Provider, model: run.ModelSnapshot.Model, providerResponseId: safeProviderResponseId);
        var unmarkedCandidate = Append(run, observedNow, [observed, completed]);
        var integrityError = ValidateProviderResult(unmarkedCandidate, result, iteration, step.Id, attempt, out _);
        if (sequentialNode is not null)
        {
            if (integrityError is null)
            {
                completed = WithSequentialEvidence(completed, sequentialNode, CustomLoopSequentialNodeEvidenceKind.CompletedOutcome, CustomLoopSequentialNodeDisposition.Completed);
            }
            else
            {
                observed = WithSequentialEvidence(observed, sequentialNode, CustomLoopSequentialNodeEvidenceKind.AmbiguityAttention, CustomLoopSequentialNodeDisposition.NeedsReview);
            }
        }

        var observedCandidate = Append(run, observedNow, [observed, completed]);
        var observedPersisted = await PersistAsync(run, observedCandidate, IntegrityToken(), outcomeMayExist: true);
        if (observedPersisted.Terminal is not null)
        {
            return observedPersisted;
        }

        run = observedPersisted.Run!;
        var retainedIntegrityError = ValidateProviderResult(run, result, iteration, step.Id, attempt, out var durableToolRequestsConsumed);
        if (!string.Equals(integrityError, retainedIntegrityError, StringComparison.Ordinal))
        {
            var terminal = await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, "provider_result_reconciliation_mismatch", "The provider result changed classification after its exact durable evidence append; automatic replay is forbidden.");
            return new RunAdvance(terminal.Run, terminal);
        }
        SequentialAuditBoundaryFailure? auditFailure;
        try
        {
            var outcome = integrityError is null ? AuditSchema.Outcomes.Succeeded : AuditSchema.Outcomes.NeedsReview;
            var terminalEventId = integrityError is null ? completed.EventId : observed.EventId;
            var terminalEvent = run.Events.Single(item => string.Equals(item.EventId, terminalEventId, StringComparison.Ordinal));
            auditFailure = await AppendOutcomeAuditAsync(
                run,
                terminalEvent,
                AttemptAudit(actor, run, step.Id, iteration, correlation, assembly, AuditSchema.Actions.LoopNodeAttempt, outcome, canonical, result, attempt: attempt),
                sequentialNode?.AuditRecorder,
                IntegrityToken());
        }
        catch (Exception exception)
        {
            var terminal = await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, "attempt_outcome_audit_failed", $"The provider outcome is evidence, but its matching audit could not be recorded: {SafeExceptionClass(exception)}.");
            return new RunAdvance(terminal.Run, terminal);
        }

        if (auditFailure is not null)
        {
            var terminal = await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, auditFailure.FailureCode, auditFailure.Detail);
            return new RunAdvance(terminal.Run, terminal);
        }

        if (integrityError is not null)
        {
            var terminal = await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, "provider_result_mismatch", integrityError);
            return new RunAdvance(terminal.Run, terminal);
        }

        var publicationBoundary = await RefreshControlUpdateAsync(run);
        if (publicationBoundary.Terminal is not null)
        {
            return publicationBoundary;
        }

        run = publicationBoundary.Run!;
        var published = run.Status == CustomLoopRunStatus.CancelRequested
            ? new RunAdvance(run, null)
            : await PublishIfSelectedAsync(run, assembly.ResolvedOutputPolicy, retained, step.Id, isExit: false, actor, sequentialNode?.AllowedCapabilityIds);
        if (published.Terminal is not null)
        {
            return published;
        }

        run = published.Run!;
        var nextStepIndex = run.Checkpoint.NextStepIndex + 1;
        var earlier = assembly.ResolvedOutputPolicy.RetainForLoopReasoning ? [.. run.Checkpoint.EarlierRetainedOutputs, retained] : run.Checkpoint.EarlierRetainedOutputs;
        var checkpoint = run.Checkpoint with
        {
            NextStepIndex = nextStepIndex,
            PendingExitDecision = nextStepIndex == run.AdmittedDefinition.InferenceSteps.Length && run.AdmittedDefinition.ExitPolicy.MaxAdditionalIterations > run.Checkpoint.AcceptedRepeatCount,
            EarlierRetainedOutputs = earlier,
            CurrentIterationResult = retained,
            ToolRequestsUsed = checked(run.Checkpoint.ToolRequestsUsed + durableToolRequestsConsumed)
        };
        if (deferCheckpoint)
        {
            return new RunAdvance(run, null, checkpoint);
        }

        return await CommitCheckpointAsync(run, checkpoint, $"Inference checkpoint committed after `{step.Id}`.");
    }

    private async Task<RunAdvance> ExecuteExitAsync(CustomLoopRunRecord run, string actor, ProviderDispatchState dispatchState, CancellationToken cancellationToken)
    {
        CustomLoopContextAssembly assembly;
        CustomLoopToolAuthoritySnapshot authority;
        try
        {
            var capabilityFailure = await GetCapabilityFailureAsync(run, cancellationToken);
            if (capabilityFailure is not null)
            {
                throw new InvalidOperationException(capabilityFailure);
            }

            authority = await _authorityProvider.ResolveAsync(run.AdmittedDefinition.RoleId, [], cancellationToken);
            EnsureAuthorityBound(run, authority, []);

            assembly = _contextResolver.ResolveExit(run);
            EnsureRequestBound(assembly);
            EnsureAttemptBound(run);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var cancelled = await CancelBeforeDispatchAsync(run, actor);
            return new RunAdvance(cancelled.Run, cancelled);
        }
        catch (Exception exception)
        {
            var terminal = await TerminateAsync(run, actor, CustomLoopRunStatus.Failed, "invalid_exit_request", $"The Exit request could not be assembled safely: {SafeExceptionClass(exception)}.");
            return new RunAdvance(terminal.Run, terminal);
        }

        var iteration = run.Checkpoint.Iteration;
        var correlation = NewCorrelationId("exit");
        var now = Now(run);
        var startedEvent = Event(run, now, CustomLoopRunEventKind.ExitDecisionStarted, "Exit-decision trace committed before tool-less provider dispatch.", iteration, "exit", 1, assembly.Blocks, provider: run.ModelSnapshot.Provider, model: run.ModelSnapshot.Model, providerResponseId: correlation, toolAuthority: authority, traceReservationUtf8Bytes: CustomLoopLimits.MaxAttemptEvidenceReservationUtf8Bytes);
        var startedCandidate = Append(run, now, [startedEvent]);
        var capacityBoundary = await RejectUnavailableTraceCapacityAsync(run, startedCandidate, actor, "Exit", cancellationToken);
        if (capacityBoundary is not null)
        {
            return capacityBoundary;
        }

        RunAdvance started;
        try
        {
            started = await PersistAsync(run, startedCandidate, cancellationToken, outcomeMayExist: false, propagateCancellation: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var cancelled = await CancelAfterInterruptedPreDispatchPersistenceAsync(run, startedCandidate, actor);
            return new RunAdvance(cancelled.Run, cancelled);
        }

        if (started.Terminal is not null)
        {
            return started;
        }

        run = started.Run!;
        try
        {
            await _auditLog.AppendAsync(AttemptAudit(actor, run, "exit", iteration, correlation, assembly, AuditSchema.Actions.LoopExitDecision, AuditSchema.Outcomes.Started, null, null), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var cancelled = await CancelBeforeDispatchAsync(run, actor);
            return new RunAdvance(cancelled.Run, cancelled);
        }
        catch (Exception exception)
        {
            var terminal = await TerminateAsync(run, actor, CustomLoopRunStatus.Failed, "exit_start_audit_failed", $"The Exit-start audit could not be recorded before dispatch: {SafeExceptionClass(exception)}.");
            return new RunAdvance(terminal.Run, terminal);
        }

        var attemptRequest = new CustomLoopInferenceAttemptRequest(
            run.Id,
            run.LoopId,
            run.AdmittedDefinition.RoleId,
            run.AdmittedDefinition.DefinitionVersion,
            run.AdmittedDefinition.ContentHash,
            iteration,
            "exit",
            1,
            correlation,
            IsExit: true,
            AllowTools: false,
            run.ModelSnapshot,
            Array.Empty<CustomLoopToolAssignment>(),
            run.Checkpoint.ToolRequestsUsed,
            assembly.Request,
            authority)
        {
            CapabilityAdmission = run.CapabilityAdmission
        };

        CustomLoopInferenceAttemptResult result;
        // Exit inference has the same effect boundary as an ordinary step. Once dispatch starts,
        // cancellation and faults are evidence-uncertain rather than safe pre-invocation failures.
        var providerInvoked = false;
        ICustomLoopAttemptCancellationRegistration? cancellationRegistration = null;
        using var providerBoundaryToken = CreateProviderToken(run, cancellationToken);
        using var providerToken = CancellationTokenSource.CreateLinkedTokenSource(providerBoundaryToken.Token);
        if (!_activeAttemptCancellations.TryAdd(run.Id, providerToken))
        {
            return await RecordAttemptFailureAsync(run, actor, "exit", iteration, correlation, assembly, new InvalidOperationException("A provider attempt is already registered for this run."), isExit: true, providerWasInvoked: false);
        }

        try
        {
            cancellationRegistration = _attemptCancellationBroker?.RegisterActiveAttempt(run.Id, providerToken, providerBoundaryToken.Token);
            var dispatchBoundary = await ObserveControlBoundaryAsync(run, actor);
            if (dispatchBoundary.Terminal is not null)
            {
                return dispatchBoundary;
            }

            run = dispatchBoundary.Run!;
            if (ExecutionDeadlineReached(run))
            {
                var terminal = await TerminateAsync(run, actor, CustomLoopRunStatus.Failed, "run_deadline_exceeded", "The custom-loop execution deadline was reached before the Exit provider request could start.");
                return new RunAdvance(terminal.Run, terminal);
            }

            providerToken.Token.ThrowIfCancellationRequested();
            result = await _inferenceExecutor.ExecuteAsync(attemptRequest, providerToken.Token, () =>
            {
                providerInvoked = true;
                dispatchState.MarkProviderRequestStarted();
            });
        }
        catch (OperationCanceledException) when (!providerInvoked)
        {
            return await HandlePreInvocationCancellationAsync(run, actor, cancellationToken);
        }
        catch (OperationCanceledException exception)
        {
            cancellationRegistration?.TryConfirmProviderInterruption(exception.CancellationToken);
            return await RecordAttemptFailureAsync(run, actor, "exit", iteration, correlation, assembly, exception, isExit: true, providerWasInvoked: true);
        }
        catch (Exception exception)
        {
            return await RecordAttemptFailureAsync(run, actor, "exit", iteration, correlation, assembly, exception, isExit: true, providerWasInvoked: providerInvoked);
        }
        finally
        {
            cancellationRegistration?.Dispose();
            _activeAttemptCancellations.TryRemove(run.Id, out _);
        }

        if (result is null)
        {
            return await RecordAttemptFailureAsync(run, actor, "exit", iteration, correlation, assembly, new InvalidOperationException("Provider executor returned no result."), isExit: true, providerWasInvoked: providerInvoked);
        }

        var refreshed = await RefreshControlUpdateAsync(run);
        if (refreshed.Terminal is not null)
        {
            return refreshed;
        }

        run = refreshed.Run!;

        var decision = ParseExitDecision(result.OutputText ?? string.Empty);
        var canonical = Canonicalize(result.OutputText);
        var publicationId = assembly.ResolvedOutputPolicy.PublishToInvokingConversation ? PublicationOperationId(run.Id, iteration, "exit", isExit: true) : null;
        var observedNow = Now(run);
        var safeProviderResponseId = SafeReference(result.ProviderResponseId);
        var observed = Event(run, observedNow, CustomLoopRunEventKind.NodeOutcomeObserved, "Exit provider outcome was observed and retained as local evidence.", iteration, "exit", 1, output: canonical.Text, originalOutputCharacters: canonical.OriginalCharacterCount, truncated: canonical.Truncated, retained: assembly.ResolvedOutputPolicy.RetainForLoopReasoning, published: assembly.ResolvedOutputPolicy.PublishToInvokingConversation, publicationId: publicationId, provider: run.ModelSnapshot.Provider, model: run.ModelSnapshot.Model, providerResponseId: safeProviderResponseId, exitDecision: decision);
        var completed = Event(run with { Events = [.. run.Events, observed] }, observedNow, CustomLoopRunEventKind.ExitDecisionCompleted, decision == CustomLoopExitDecision.Invalid ? "Exit returned an invalid decision; another iteration is forbidden." : $"Exit returned the exact governed `{decision}` decision.", iteration, "exit", 1, output: canonical.Text, originalOutputCharacters: canonical.OriginalCharacterCount, truncated: canonical.Truncated, retained: assembly.ResolvedOutputPolicy.RetainForLoopReasoning, published: assembly.ResolvedOutputPolicy.PublishToInvokingConversation, publicationId: publicationId, provider: run.ModelSnapshot.Provider, model: run.ModelSnapshot.Model, providerResponseId: safeProviderResponseId, exitDecision: decision);
        var observedPersisted = await PersistAsync(run, Append(run, observedNow, [observed, completed]), IntegrityToken(), outcomeMayExist: true);
        if (observedPersisted.Terminal is not null)
        {
            return observedPersisted;
        }

        run = observedPersisted.Run!;
        var integrityError = ValidateProviderResult(run, result, iteration, "exit", 1, out var durableToolRequestsConsumed);
        if (integrityError is null && durableToolRequestsConsumed != 0)
        {
            integrityError = "The tool-less Exit attempt reported a governed tool call and cannot be trusted.";
        }

        try
        {
            var outcome = integrityError is not null || decision == CustomLoopExitDecision.Invalid ? AuditSchema.Outcomes.NeedsReview : AuditSchema.Outcomes.Succeeded;
            await _auditLog.AppendAsync(AttemptAudit(actor, run, "exit", iteration, correlation, assembly, AuditSchema.Actions.LoopExitDecision, outcome, canonical, result, decision), IntegrityToken());
        }
        catch (Exception exception)
        {
            var terminal = await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, "exit_outcome_audit_failed", $"The Exit outcome is evidence, but its matching audit could not be recorded: {SafeExceptionClass(exception)}.");
            return new RunAdvance(terminal.Run, terminal);
        }

        if (integrityError is not null)
        {
            var terminal = await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, "exit_provider_result_mismatch", integrityError);
            return new RunAdvance(terminal.Run, terminal);
        }

        if (decision == CustomLoopExitDecision.Invalid)
        {
            var checkpoint = run.Checkpoint with { PendingExitDecision = false };
            var committed = await CommitCheckpointAsync(run, checkpoint, "Invalid Exit outcome checkpoint committed; traversal will not repeat.");
            if (committed.Terminal is not null)
            {
                return committed;
            }

            var terminal = await TerminateAsync(committed.Run!, actor, CustomLoopRunStatus.NeedsReview, "invalid_exit_decision", "Exit must return only the exact trimmed ASCII token `Complete` or `Repeat`; no repeat was started.");
            return new RunAdvance(terminal.Run, terminal);
        }

        var iterationResult = run.Checkpoint.CurrentIterationResult;
        if (iterationResult is null)
        {
            var terminal = await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, "missing_iteration_result", "Exit completed without a durable final inference result.");
            return new RunAdvance(terminal.Run, terminal);
        }

        var publicationBoundary = await RefreshControlUpdateAsync(run);
        if (publicationBoundary.Terminal is not null)
        {
            return publicationBoundary;
        }

        run = publicationBoundary.Run!;
        var published = run.Status == CustomLoopRunStatus.CancelRequested
            ? new RunAdvance(run, null)
            : await PublishIfSelectedAsync(run, assembly.ResolvedOutputPolicy, iterationResult, "exit", isExit: true, actor);
        if (published.Terminal is not null)
        {
            return published;
        }

        run = published.Run!;
        var exitCheckpoint = run.Checkpoint with { PendingExitDecision = false };
        var exitCommitted = await CommitCheckpointAsync(run, exitCheckpoint, $"Exit `{decision}` checkpoint committed.");
        if (exitCommitted.Terminal is not null)
        {
            return exitCommitted;
        }

        run = exitCommitted.Run!;
        if (run.Status == CustomLoopRunStatus.CancelRequested)
        {
            return new RunAdvance(run, null);
        }

        if (decision == CustomLoopExitDecision.Complete)
        {
            var completionBoundary = await ObserveControlBoundaryAsync(run, actor);
            if (completionBoundary.Terminal is not null)
            {
                return completionBoundary;
            }

            run = completionBoundary.Run!;
            var terminal = await TerminateAsync(run, actor, CustomLoopRunStatus.Completed, null, "Exit completed the loop.", iterationResult.Content);
            return new RunAdvance(terminal.Run, terminal);
        }

        var repeated = run.Checkpoint with
        {
            Iteration = run.Checkpoint.Iteration + 1,
            NextStepIndex = 0,
            AcceptedRepeatCount = run.Checkpoint.AcceptedRepeatCount + 1,
            PendingExitDecision = false,
            EarlierRetainedOutputs = [],
            PreviousIterationResult = assembly.ResolvedOutputPolicy.RetainForLoopReasoning ? iterationResult : null,
            CurrentIterationResult = null
        };
        return await CommitCheckpointAsync(run, repeated, "Repeat boundary committed; traversal restarts at the first persisted inference step.");
    }

    private async Task<CustomLoopOrderedRunResult> CompleteDeterministicallyAsync(CustomLoopRunRecord run, string actor, string detail, CancellationToken cancellationToken)
    {
        var prepared = await PrepareDeterministicExitAsync(run, actor, detail, cancellationToken);
        if (prepared.Terminal is not null)
        {
            return prepared.Terminal;
        }

        return await CommitPreparedSequentialAdvancementAsync(prepared, actor, detail);
    }

    private async Task<RunAdvance> PrepareDeterministicExitAsync(
        CustomLoopRunRecord run,
        string actor,
        string detail,
        CancellationToken cancellationToken,
        SequentialNodeExecutionContext? sequentialNode = null)
    {
        var iterationResult = run.Checkpoint.CurrentIterationResult;
        if (iterationResult is null)
        {
            var terminal = await TerminateAsync(run, actor, CustomLoopRunStatus.Failed, "missing_iteration_result", "Deterministic Exit could not find the final inference result.");
            return new RunAdvance(terminal.Run, terminal);
        }

        CustomLoopContextOutputPolicy outputPolicy;
        try
        {
            outputPolicy = CustomLoopContextResolver.ResolvePolicy(run.AdmittedDefinition.ExitPolicy.ContextPolicy, run.AdmittedDefinition.ContextDefaults.Exit).ContextOut;
        }
        catch (Exception exception)
        {
            if (sequentialNode is not null)
            {
                return await RejectSequentialNodeBeforeProviderAsync(
                    run,
                    actor,
                    sequentialNode,
                    "exit",
                    isExit: true,
                    "invalid_exit_policy",
                    $"The deterministic Exit policy is invalid: {SafeExceptionClass(exception)}.");
            }

            var terminal = await TerminateAsync(run, actor, CustomLoopRunStatus.Failed, "invalid_exit_policy", $"The deterministic Exit policy is invalid: {SafeExceptionClass(exception)}.");
            return new RunAdvance(terminal.Run, terminal);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            if (sequentialNode is not null)
            {
                return await RejectSequentialNodeBeforeProviderAsync(
                    run,
                    actor,
                    sequentialNode,
                    "exit",
                    isExit: true,
                    failureCode: null,
                    CanonicalCallerCancellationDetail,
                    CustomLoopRunStatus.Cancelled);
            }

            var cancelled = await CancelBeforeDispatchAsync(run, actor);
            return new RunAdvance(cancelled.Run, cancelled);
        }

        if (sequentialNode is not null)
        {
            string? capabilityFailure;
            try
            {
                capabilityFailure = await GetCapabilityFailureAsync(run, cancellationToken, sequentialNode.AllowedCapabilityIds);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return await RejectSequentialNodeBeforeProviderAsync(
                    run,
                    actor,
                    sequentialNode,
                    "exit",
                    isExit: true,
                    "canonical_exit_capability_check_failed",
                    $"Canonical Exit capability revalidation could not complete: {SafeExceptionClass(exception)}.");
            }

            if (capabilityFailure is not null)
            {
                return await RejectSequentialNodeBeforeProviderAsync(
                    run,
                    actor,
                    sequentialNode,
                    "exit",
                    isExit: true,
                    "canonical_exit_capability_invalid",
                    capabilityFailure);
            }
        }

        var exitEvents = new List<CustomLoopRunEvent>();
        if (sequentialNode is not null)
        {
            var started = Event(
                run,
                Now(run),
                CustomLoopRunEventKind.ExitDecisionStarted,
                "Deterministic canonical Exit dispatch was retained before evaluation.",
                run.Checkpoint.Iteration,
                "exit",
                sequentialNode.Attempt,
                traceReservationUtf8Bytes: CustomLoopLimits.MaxAttemptEvidenceReservationUtf8Bytes);
            exitEvents.Add(WithSequentialEvidence(started, sequentialNode, CustomLoopSequentialNodeEvidenceKind.DispatchStarted, CustomLoopSequentialNodeDisposition.Unknown));
        }

        var exitOwner = exitEvents.Count == 0 ? run : run with { Events = [.. run.Events, .. exitEvents] };
        var exitEvent = Event(
            exitOwner,
            Now(run),
            CustomLoopRunEventKind.ExitDecisionCompleted,
            detail,
            run.Checkpoint.Iteration,
            "exit",
            sequentialNode?.Attempt ?? 1,
            retained: outputPolicy.RetainForLoopReasoning,
            published: outputPolicy.PublishToInvokingConversation,
            publicationId: outputPolicy.PublishToInvokingConversation ? PublicationOperationId(run.Id, run.Checkpoint.Iteration, "exit", isExit: true) : null,
            exitDecision: CustomLoopExitDecision.Complete);
        exitEvents.Add(sequentialNode is null
            ? exitEvent
            : WithSequentialEvidence(exitEvent, sequentialNode, CustomLoopSequentialNodeEvidenceKind.CompletedOutcome, CustomLoopSequentialNodeDisposition.Completed));
        var exitCandidate = Append(run, exitEvent.TimestampUtc, exitEvents);
        RunAdvance exitPersisted;
        try
        {
            exitPersisted = await PersistAsync(run, exitCandidate, cancellationToken, outcomeMayExist: false, propagateCancellation: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var cancelled = await CancelAfterInterruptedPreDispatchPersistenceAsync(run, exitCandidate, actor);
            return new RunAdvance(cancelled.Run, cancelled);
        }

        if (exitPersisted.Terminal is not null)
        {
            return exitPersisted;
        }

        run = exitPersisted.Run!;
        SequentialAuditBoundaryFailure? auditFailure;
        try
        {
            var durableExitEvent = run.Events.Single(item => string.Equals(item.EventId, exitEvent.EventId, StringComparison.Ordinal));
            auditFailure = await AppendOutcomeAuditAsync(
                run,
                durableExitEvent,
                CreateDeterministicExitAudit(run, actor, detail, durableExitEvent),
                sequentialNode?.AuditRecorder,
                IntegrityToken());
        }
        catch (Exception exception)
        {
            var terminal = await TerminateAsync(run, actor, sequentialNode is null ? CustomLoopRunStatus.Failed : CustomLoopRunStatus.NeedsReview, sequentialNode is null ? "deterministic_exit_audit_failed" : "canonical_outcome_audit_unavailable", $"The deterministic Exit audit could not be recorded: {SafeExceptionClass(exception)}.");
            return new RunAdvance(terminal.Run, terminal);
        }

        if (auditFailure is not null)
        {
            var terminal = await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, auditFailure.FailureCode, auditFailure.Detail);
            return new RunAdvance(terminal.Run, terminal);
        }

        var publicationBoundary = await RefreshControlUpdateAsync(run);
        if (publicationBoundary.Terminal is not null)
        {
            return publicationBoundary;
        }

        run = publicationBoundary.Run!;
        var published = run.Status == CustomLoopRunStatus.CancelRequested
            ? new RunAdvance(run, null)
            : await PublishIfSelectedAsync(run, outputPolicy, iterationResult, "exit", isExit: true, actor, sequentialNode?.AllowedCapabilityIds);
        if (published.Terminal is not null)
        {
            return published;
        }

        run = published.Run!;
        var checkpoint = run.Checkpoint with { PendingExitDecision = false };
        return new RunAdvance(
            run,
            null,
            checkpoint,
            new PendingTerminal(CustomLoopRunStatus.Completed, null, detail, iterationResult.Content));
    }

    private async Task<CustomLoopOrderedRunResult> CommitPreparedSequentialAdvancementAsync(
        RunAdvance prepared,
        string actor,
        string detail)
    {
        var committed = await CommitCheckpointAsync(prepared.Run!, prepared.PendingCheckpoint!, detail);
        if (committed.Terminal is not null)
        {
            return committed.Terminal;
        }

        var completionBoundary = await ObserveControlBoundaryAsync(committed.Run!, actor);
        var terminal = prepared.PendingTerminal!;
        return completionBoundary.Terminal ?? await TerminateAsync(
            completionBoundary.Run!,
            actor,
            terminal.Status,
            terminal.FailureCode,
            terminal.Detail,
            terminal.FinalOutput);
    }

    private async Task<RunAdvance> PublishIfSelectedAsync(
        CustomLoopRunRecord run,
        CustomLoopContextOutputPolicy policy,
        CustomLoopRetainedOutput output,
        string stepId,
        bool isExit,
        string actor,
        IReadOnlyCollection<CapabilityId>? allowedCapabilityIds = null)
    {
        if (!policy.PublishToInvokingConversation)
        {
            return new RunAdvance(run, null);
        }

        var operationId = PublicationOperationId(run.Id, run.Checkpoint.Iteration, stepId, isExit);
        var conversation = run.InvokingConversation;
        var intents = run.Events.Where(item => item.Kind == CustomLoopRunEventKind.ConversationPublicationStarted
            && item.Iteration == run.Checkpoint.Iteration
            && string.Equals(item.StepId, stepId, StringComparison.Ordinal)
            && string.Equals(item.ConversationPublicationId, operationId, StringComparison.Ordinal)).ToArray();
        var outcomes = run.Events.Where(item => item.Kind == CustomLoopRunEventKind.ConversationPublished
            && item.Iteration == run.Checkpoint.Iteration
            && string.Equals(item.StepId, stepId, StringComparison.Ordinal)
            && string.Equals(item.ConversationPublicationId, operationId, StringComparison.Ordinal)).ToArray();
        if (intents.Length > 1 || outcomes.Length > 1 || conversation is not null && outcomes.Length == 1 && intents.Length != 1)
        {
            var terminal = await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, "invalid_conversation_publication_history", "The stable conversation-publication operation has duplicate or causally incomplete durable evidence.");
            return new RunAdvance(terminal.Run, terminal);
        }

        if (outcomes.Length == 1)
        {
            var outcome = outcomes[0];
            if (conversation is null
                && intents.Length == 0
                && outcome.PublishedToInvokingConversation == false
                && outcome.CanonicalOutput is null
                && string.Equals(outcome.Detail, PublicationOmittedDetail, StringComparison.Ordinal))
            {
                return new RunAdvance(run, null);
            }

            if (conversation is not null
                && outcome.PublishedToInvokingConversation == true
                && string.Equals(outcome.CanonicalOutput, output.Content, StringComparison.Ordinal)
                && (string.Equals(outcome.Detail, PublicationPublishedDetail, StringComparison.Ordinal)
                    || string.Equals(outcome.Detail, PublicationAlreadyPublishedDetail, StringComparison.Ordinal)))
            {
                return new RunAdvance(run, null);
            }

            var definitelyFailed = string.Equals(outcome.Detail, PublicationDefinitelyFailedDetail, StringComparison.Ordinal);
            var terminalStatus = definitelyFailed ? CustomLoopRunStatus.Failed : CustomLoopRunStatus.NeedsReview;
            var failureCode = definitelyFailed ? "conversation_publication_failed" : "conversation_publication_uncertain";
            var terminal = await TerminateAsync(run, actor, terminalStatus, failureCode, definitelyFailed
                ? "Conversation publication definitely failed and was not reported as success."
                : "Conversation publication evidence is incomplete, divergent, or uncertain and requires review.");
            return new RunAdvance(terminal.Run, terminal);
        }

        if (conversation is null)
        {
            var omitted = Event(run, Now(run), CustomLoopRunEventKind.ConversationPublished, PublicationOmittedDetail, run.Checkpoint.Iteration, stepId, published: false, publicationId: operationId);
            return await PersistAsync(run, Append(run, omitted.TimestampUtc, [omitted]), IntegrityToken(), outcomeMayExist: false);
        }

        CustomLoopPriorConversationPublication[] priorPublications;
        try
        {
            priorPublications = GetPriorConversationPublications(run);
        }
        catch (Exception exception)
        {
            var terminal = await TerminateAsync(run, actor, CustomLoopRunStatus.Failed, "invalid_conversation_publication_history", $"The durable conversation-publication history could not be reconstructed safely: {SafeExceptionClass(exception)}.");
            return new RunAdvance(terminal.Run, terminal);
        }

        // Commit the stable operation identity before dispatch. Recovery can then retry the same
        // idempotent append without inventing a second conversation publication.
        if (intents.Length == 0)
        {
            var intent = Event(run, Now(run), CustomLoopRunEventKind.ConversationPublicationStarted, "Conversation publication intent committed before the idempotent append.", run.Checkpoint.Iteration, stepId, publicationId: operationId);
            var intentPersisted = await PersistAsync(run, Append(run, intent.TimestampUtc, [intent]), IntegrityToken(), outcomeMayExist: false);
            if (intentPersisted.Terminal is not null)
            {
                return intentPersisted;
            }

            run = intentPersisted.Run!;
        }
        CustomLoopConversationPublicationResult publication;
        var publicationDispatched = false;
        ICustomLoopAttemptCancellationRegistration? cancellationRegistration = null;
        using var publicationBoundaryToken = new CancellationTokenSource(_integrityWriteTimeout);
        using var publicationToken = CancellationTokenSource.CreateLinkedTokenSource(publicationBoundaryToken.Token);
        if (!_activeAttemptCancellations.TryAdd(run.Id, publicationToken))
        {
            var terminal = await TerminateAsync(run, actor, CustomLoopRunStatus.Failed, "publication_registration_failed", "Conversation publication could not be registered with the active cancellation protocol, so no append was attempted.");
            return new RunAdvance(terminal.Run, terminal);
        }

        try
        {
            cancellationRegistration = _attemptCancellationBroker?.RegisterActiveAttempt(run.Id, publicationToken, publicationBoundaryToken.Token);
        }
        catch (Exception exception)
        {
            _activeAttemptCancellations.TryRemove(run.Id, out _);
            var terminal = await TerminateAsync(run, actor, CustomLoopRunStatus.Failed, "publication_registration_failed", $"Conversation publication could not register with the workspace-host cancellation broker, so no append was attempted: {SafeExceptionClass(exception)}.");
            return new RunAdvance(terminal.Run, terminal);
        }

        try
        {
            var publicationBoundary = await RefreshControlUpdateAsync(run);
            if (publicationBoundary.Terminal is not null)
            {
                return publicationBoundary;
            }

            run = publicationBoundary.Run!;
            if (run.Status == CustomLoopRunStatus.CancelRequested)
            {
                return new RunAdvance(run, null);
            }

            string? capabilityFailure;
            try
            {
                capabilityFailure = await GetCapabilityFailureAsync(run, publicationToken.Token, allowedCapabilityIds);
            }
            catch (OperationCanceledException) when (publicationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var terminal = await TerminateAsync(run, actor, CustomLoopRunStatus.Failed, "capability_revalidation_check_failed_before_publication", $"Custom-loop capability revalidation could not complete before conversation publication: {SafeExceptionClass(exception)}.");
                return new RunAdvance(terminal.Run, terminal);
            }

            if (capabilityFailure is not null)
            {
                var terminal = await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, "capability_revalidation_failed_before_publication", $"Custom-loop capability revalidation failed closed before conversation publication: {capabilityFailure}");
                return new RunAdvance(terminal.Run, terminal);
            }

            var request = new CustomLoopConversationPublicationRequest(operationId, run.Id, run.LoopId, run.Checkpoint.Iteration, stepId, conversation.ConversationId, conversation.CapturedVersion, output.Content, output.ContentHash, priorPublications, () => publicationDispatched = true);
            publicationToken.Token.ThrowIfCancellationRequested();
            publication = await _conversationPublisher.PublishAsync(request, publicationToken.Token);
        }
        catch (OperationCanceledException) when (!publicationDispatched && publicationToken.IsCancellationRequested)
        {
            var cancellationBoundary = await RefreshControlUpdateAsync(run);
            if (cancellationBoundary.Terminal is not null)
            {
                return cancellationBoundary;
            }

            if (cancellationBoundary.Run!.Status == CustomLoopRunStatus.CancelRequested)
            {
                return new RunAdvance(cancellationBoundary.Run, null);
            }

            var terminal = await TerminateAsync(cancellationBoundary.Run, actor, CustomLoopRunStatus.Failed, "publication_cancelled_before_dispatch", "Conversation publication was cancelled before the append began, but no durable cancellation request could be confirmed.");
            return new RunAdvance(terminal.Run, terminal);
        }
        catch (Exception exception)
        {
            publication = new CustomLoopConversationPublicationResult(CustomLoopConversationPublicationOutcome.Uncertain, null, $"Publisher threw {SafeExceptionClass(exception)} after publication may have occurred.");
        }
        finally
        {
            cancellationRegistration?.Dispose();
            _activeAttemptCancellations.TryRemove(run.Id, out _);
        }

        publication ??= new CustomLoopConversationPublicationResult(CustomLoopConversationPublicationOutcome.Uncertain, null, "Publisher returned no result after publication may have occurred.");

        var publicationIdMatches = string.Equals(publication.PublicationId, operationId, StringComparison.Ordinal);
        var isPublished = publicationIdMatches && (publication.Outcome is CustomLoopConversationPublicationOutcome.Published or CustomLoopConversationPublicationOutcome.AlreadyPublished);
        var publicationId = operationId;
        var eventDetail = !publicationIdMatches
            ? PublicationMismatchedIdentityDetail
            : publication.Outcome switch
            {
                CustomLoopConversationPublicationOutcome.Published => PublicationPublishedDetail,
                CustomLoopConversationPublicationOutcome.AlreadyPublished => PublicationAlreadyPublishedDetail,
                CustomLoopConversationPublicationOutcome.DefinitelyFailed => PublicationDefinitelyFailedDetail,
                CustomLoopConversationPublicationOutcome.Uncertain => PublicationUncertainDetail,
                _ => PublicationUnsupportedDetail,
            };
        var publicationEvent = Event(run, Now(run), CustomLoopRunEventKind.ConversationPublished, eventDetail, run.Checkpoint.Iteration, stepId, output: isPublished ? output.Content : null, originalOutputCharacters: isPublished ? output.Content.Length : null, truncated: isPublished ? false : null, published: isPublished, publicationId: publicationId);
        var persisted = await PersistAsync(run, Append(run, publicationEvent.TimestampUtc, [publicationEvent]), IntegrityToken(), outcomeMayExist: publication.Outcome != CustomLoopConversationPublicationOutcome.DefinitelyFailed);
        if (persisted.Terminal is not null)
        {
            return persisted;
        }

        run = persisted.Run!;
        if (publication.Outcome == CustomLoopConversationPublicationOutcome.DefinitelyFailed)
        {
            var terminal = await TerminateAsync(run, actor, CustomLoopRunStatus.Failed, "conversation_publication_failed", "Conversation publication definitely failed and was not reported as success.");
            return new RunAdvance(terminal.Run, terminal);
        }

        if (!isPublished)
        {
            var terminal = await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, "conversation_publication_uncertain", "Conversation publication was not definitely committed or rejected and requires review.");
            return new RunAdvance(terminal.Run, terminal);
        }

        return new RunAdvance(run, null);
    }

    private async Task<RunAdvance> CommitCheckpointAsync(CustomLoopRunRecord run, CustomLoopRunCheckpoint checkpoint, string detail)
    {
        var now = Now(run);
        var checkpointEvent = Event(run, now, CustomLoopRunEventKind.CheckpointCommitted, detail, checkpoint.Iteration);
        var committedCheckpoint = checkpoint with { LastCommittedSequence = checkpointEvent.Sequence };
        var candidate = Append(run, now, [checkpointEvent]) with
        {
            Checkpoint = committedCheckpoint,
            ExecutionClock = AdvanceClock(run.ExecutionClock, now, terminal: false)
        };
        return await PersistAsync(run, candidate, IntegrityToken(), outcomeMayExist: true);
    }

    private async Task<RunAdvance> RecordAttemptFailureAsync(
        CustomLoopRunRecord run,
        string actor,
        string stepId,
        int iteration,
        string correlation,
        CustomLoopContextAssembly assembly,
        Exception exception,
        bool isExit,
        bool providerWasInvoked,
        SequentialNodeExecutionContext? sequentialNode = null,
        CustomLoopRunStatus? terminalStatusOverride = null,
        string? failureCodeOverride = null,
        string? terminalDetailOverride = null)
    {
        var attempt = sequentialNode?.Attempt ?? 1;
        var refreshed = await RefreshControlUpdateAsync(run);
        if (refreshed.Terminal is not null)
        {
            return refreshed;
        }

        run = refreshed.Run!;
        if (sequentialNode is not null && !providerWasInvoked && run.Status == CustomLoopRunStatus.CancelRequested)
        {
            terminalStatusOverride = CustomLoopRunStatus.Cancelled;
            failureCodeOverride = null;
            terminalDetailOverride = CanonicalDurableCancellationDetail;
        }

        var uncertain = providerWasInvoked && IsUncertainProviderFailure(exception);
        var needsReview = providerWasInvoked && (isExit || uncertain);
        var detail = terminalDetailOverride ?? (!providerWasInvoked
            ? $"Provider setup failed before dispatch: {SafeExceptionClass(exception)}."
            : uncertain
                ? "Provider attempt failed after dispatch and its outcome cannot be proven."
                : $"Provider attempt failed without an automatic retry: {SafeExceptionClass(exception)}.");
        var failure = Event(run, Now(run), CustomLoopRunEventKind.NodeAttemptFailed, detail, iteration, stepId, attempt, provider: run.ModelSnapshot.Provider, model: run.ModelSnapshot.Model, providerResponseId: correlation);
        if (sequentialNode is not null)
        {
            failure = WithSequentialEvidence(
                failure,
                sequentialNode,
                needsReview ? CustomLoopSequentialNodeEvidenceKind.AmbiguityAttention : CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection,
                needsReview ? CustomLoopSequentialNodeDisposition.NeedsReview : CustomLoopSequentialNodeDisposition.Rejected);
        }
        var persisted = await PersistAsync(run, Append(run, failure.TimestampUtc, [failure]), IntegrityToken(), outcomeMayExist: uncertain);
        if (persisted.Terminal is not null)
        {
            return persisted;
        }

        run = persisted.Run!;
        SequentialAuditBoundaryFailure? auditFailure = null;
        try
        {
            var action = isExit ? AuditSchema.Actions.LoopExitDecision : AuditSchema.Actions.LoopNodeAttempt;
            var outcome = needsReview ? AuditSchema.Outcomes.NeedsReview : AuditSchema.Outcomes.Failed;
            var durableFailure = run.Events.Single(item => string.Equals(item.EventId, failure.EventId, StringComparison.Ordinal));
            auditFailure = await AppendOutcomeAuditAsync(
                run,
                durableFailure,
                AttemptAudit(actor, run, stepId, iteration, correlation, assembly, action, outcome, null, null, attempt: attempt),
                sequentialNode?.AuditRecorder,
                IntegrityToken());
        }
        catch (Exception auditException)
        {
            detail = $"Provider failure evidence exists, but its outcome audit failed: {SafeExceptionClass(auditException)}.";
            uncertain = true;
            needsReview = true;
        }

        if (auditFailure is not null)
        {
            detail = auditFailure.Detail;
            uncertain = true;
            needsReview = true;
        }

        if (terminalDetailOverride is not null && auditFailure is null)
        {
            detail = terminalDetailOverride;
        }

        var status = auditFailure is not null
            ? CustomLoopRunStatus.NeedsReview
            : terminalStatusOverride ?? (needsReview ? CustomLoopRunStatus.NeedsReview : CustomLoopRunStatus.Failed);
        var code = auditFailure?.FailureCode
            ?? (terminalStatusOverride is not null
                ? failureCodeOverride
                : isExit ? "exit_attempt_failed" : uncertain ? "inference_attempt_uncertain" : "inference_attempt_failed");
        if (status == CustomLoopRunStatus.Cancelled && sequentialNode is not null)
        {
            return await CompleteSequentialCancellationAsync(run, actor, detail);
        }

        var terminal = await TerminateAsync(run, actor, status, code, detail);
        return new RunAdvance(terminal.Run, terminal);
    }

    private async Task<RunAdvance> RejectSequentialNodeBeforeProviderAsync(
        CustomLoopRunRecord run,
        string actor,
        SequentialNodeExecutionContext sequentialNode,
        string stepId,
        bool isExit,
        string? failureCode,
        string detail,
        CustomLoopRunStatus terminalStatus = CustomLoopRunStatus.Failed)
    {
        var attempt = sequentialNode.Attempt;
        var iteration = run.Checkpoint.Iteration;
        var correlation = NewCorrelationId(isExit ? "exit-rejection" : "attempt-rejection");
        var now = Now(run);
        var events = new List<CustomLoopRunEvent>();
        if (!isExit && run.Checkpoint.NextStepIndex == 0)
        {
            events.Add(Event(run, now, CustomLoopRunEventKind.IterationStarted, $"Iteration {iteration} started in persisted step order.", iteration));
        }

        var startOwner = events.Count == 0 ? run : run with { Events = [.. run.Events, .. events] };
        var started = Event(
            startOwner,
            now,
            isExit ? CustomLoopRunEventKind.ExitDecisionStarted : CustomLoopRunEventKind.NodeAttemptStarted,
            CanonicalPreProviderRejectionStartDetail,
            iteration,
            stepId,
            attempt,
            provider: run.ModelSnapshot.Provider,
            model: run.ModelSnapshot.Model,
            providerResponseId: correlation,
            traceReservationUtf8Bytes: CustomLoopLimits.MaxAttemptEvidenceReservationUtf8Bytes);
        started = WithSequentialEvidence(started, sequentialNode, CustomLoopSequentialNodeEvidenceKind.DispatchStarted, CustomLoopSequentialNodeDisposition.Unknown);
        events.Add(started);

        var failureOwner = run with { Events = [.. run.Events, .. events] };
        var failed = Event(
            failureOwner,
            now,
            CustomLoopRunEventKind.NodeAttemptFailed,
            detail,
            iteration,
            stepId,
            attempt,
            provider: run.ModelSnapshot.Provider,
            model: run.ModelSnapshot.Model,
            providerResponseId: correlation);
        failed = WithSequentialEvidence(failed, sequentialNode, CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection, CustomLoopSequentialNodeDisposition.Rejected);
        events.Add(failed);

        var persisted = await PersistAsync(run, Append(run, now, events), IntegrityToken(), outcomeMayExist: false);
        if (persisted.Terminal is not null)
        {
            return persisted;
        }

        run = persisted.Run!;
        var durableFailure = run.Events.Single(item => string.Equals(item.EventId, failed.EventId, StringComparison.Ordinal));
        var auditFailure = await AppendOutcomeAuditAsync(
            run,
            durableFailure,
            CreateCanonicalRejectionAudit(run, actor, durableFailure, isExit),
            sequentialNode.AuditRecorder,
            IntegrityToken());
        if (auditFailure is not null)
        {
            var review = await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, auditFailure.FailureCode, auditFailure.Detail);
            return new RunAdvance(review.Run, review);
        }

        if (terminalStatus == CustomLoopRunStatus.Cancelled)
        {
            return await CompleteSequentialCancellationAsync(run, actor, detail);
        }

        var terminal = await TerminateAsync(run, actor, terminalStatus, failureCode, detail);
        return new RunAdvance(terminal.Run, terminal);
    }

    private async Task<RunAdvance> CloseSequentialAttemptAtControlBoundaryAsync(
        CustomLoopRunRecord run,
        string actor,
        string stepId,
        int iteration,
        string correlation,
        CustomLoopContextAssembly assembly,
        SequentialNodeExecutionContext sequentialNode,
        CustomLoopRunStatus nextStatus,
        string detail)
    {
        var failure = Event(
            run,
            Now(run),
            CustomLoopRunEventKind.NodeAttemptFailed,
            detail,
            iteration,
            stepId,
            sequentialNode.Attempt,
            provider: run.ModelSnapshot.Provider,
            model: run.ModelSnapshot.Model,
            providerResponseId: correlation);
        failure = WithSequentialEvidence(
            failure,
            sequentialNode,
            CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection,
            CustomLoopSequentialNodeDisposition.Rejected);
        var persisted = await PersistAsync(run, Append(run, failure.TimestampUtc, [failure]), IntegrityToken(), outcomeMayExist: false);
        if (persisted.Terminal is not null)
        {
            return persisted;
        }

        run = persisted.Run!;
        var durableFailure = run.Events.Single(item => string.Equals(item.EventId, failure.EventId, StringComparison.Ordinal));
        var auditFailure = await AppendOutcomeAuditAsync(
            run,
            durableFailure,
            AttemptAudit(
                actor,
                run,
                stepId,
                iteration,
                correlation,
                assembly,
                AuditSchema.Actions.LoopNodeAttempt,
                AuditSchema.Outcomes.Failed,
                null,
                null,
                attempt: sequentialNode.Attempt),
            sequentialNode.AuditRecorder,
            IntegrityToken());
        if (auditFailure is not null)
        {
            var review = await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, auditFailure.FailureCode, auditFailure.Detail);
            return new RunAdvance(review.Run, review);
        }

        if (nextStatus == CustomLoopRunStatus.Paused)
        {
            return await PauseAtBoundaryAsync(run, actor);
        }

        if (nextStatus == CustomLoopRunStatus.Cancelled)
        {
            return await CompleteSequentialCancellationAsync(run, actor, detail);
        }

        var terminal = await TerminateAsync(run, actor, nextStatus, null, detail);
        return new RunAdvance(terminal.Run, terminal);
    }

    private async Task<RunAdvance> ObserveSequentialControlBoundaryAsync(
        CustomLoopRunRecord run,
        string actor,
        string stepId,
        int iteration,
        string correlation,
        CustomLoopContextAssembly assembly,
        SequentialNodeExecutionContext sequentialNode)
    {
        var refreshed = await RefreshControlUpdateAsync(run);
        if (refreshed.Terminal is not null)
        {
            return refreshed;
        }

        run = refreshed.Run!;
        return run.Status switch
        {
            CustomLoopRunStatus.Running => new RunAdvance(run, null),
            CustomLoopRunStatus.PauseRequested => await CloseSequentialAttemptAtControlBoundaryAsync(
                run,
                actor,
                stepId,
                iteration,
                correlation,
                assembly,
                sequentialNode,
                CustomLoopRunStatus.Paused,
                CanonicalPauseRejectionDetail),
            CustomLoopRunStatus.CancelRequested => await CloseSequentialAttemptAtControlBoundaryAsync(
                run,
                actor,
                stepId,
                iteration,
                correlation,
                assembly,
                sequentialNode,
                CustomLoopRunStatus.Cancelled,
                CanonicalDurableCancellationDetail),
            _ => new RunAdvance(null, Result(CustomLoopOrderedRunStatus.InvalidState, run, $"Ordered execution cannot dispatch from {run.Status}.")),
        };
    }

    private async Task<RunAdvance> ReconcileSequentialInferenceRejectionAsync(
        SequentialExecutionContext context,
        CustomLoopRunRecord run,
        CustomLoopInferenceStep step,
        CustomLoopRunEvent rejection,
        string actor)
    {
        var attempt = rejection.Attempt!.Value;
        var start = run.Events.LastOrDefault(item => item.Sequence < rejection.Sequence
            && item.Kind == CustomLoopRunEventKind.NodeAttemptStarted
            && item.Iteration == rejection.Iteration
            && string.Equals(item.StepId, step.Id, StringComparison.Ordinal)
            && item.Attempt == attempt
            && item.SequentialNodeEvidence is { Kind: CustomLoopSequentialNodeEvidenceKind.DispatchStarted });
        if (start is null || start.ProviderResponseId is null)
        {
            var invalid = await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, "canonical_rejection_reconciliation_failed", "The retained canonical rejection has no exact preceding dispatch marker.");
            return new RunAdvance(invalid.Run, invalid);
        }

        SequentialAuditBoundaryFailure? auditFailure;
        try
        {
            AuditEvent auditEvent;
            if (string.Equals(start.Detail, CanonicalPreProviderRejectionStartDetail, StringComparison.Ordinal))
            {
                auditEvent = CreateCanonicalRejectionAudit(run, actor, rejection, isExit: false);
            }
            else
            {
                if (start.ToolAuthority is null)
                {
                    throw new InvalidOperationException("The retained rejected attempt has no immutable authority snapshot.");
                }

                EnsureAuthorityBound(run, start.ToolAuthority, run.AdmittedDefinition.ToolAssignments);
                var effectiveAssignments = run.Checkpoint.ToolRequestsUsed < CustomLoopLimits.MaxModelVisibleGovernedToolRequestsPerRun
                    ? start.ToolAuthority.EffectiveAssignments
                    : [];
                var assembly = _contextResolver.ResolveInference(run, step, effectiveAssignments);
                EnsureRequestBound(assembly);
                if (!start.ContextBlocks.SequenceEqual(assembly.Blocks))
                {
                    throw new InvalidOperationException("The retained rejected attempt does not match the reconstructed inference request.");
                }

                auditEvent = AttemptAudit(
                    actor,
                    run,
                    step.Id,
                    rejection.Iteration!.Value,
                    start.ProviderResponseId,
                    assembly,
                    AuditSchema.Actions.LoopNodeAttempt,
                    AuditSchema.Outcomes.Failed,
                    null,
                    null,
                    attempt: attempt);
            }

            auditFailure = await AppendOutcomeAuditAsync(
                run,
                rejection,
                auditEvent,
                context.AuditRecorder,
                IntegrityToken());
        }
        catch (Exception exception)
        {
            var invalid = await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, "canonical_rejection_reconciliation_failed", $"The retained canonical rejection could not be authenticated and re-audited: {SafeExceptionClass(exception)}.");
            return new RunAdvance(invalid.Run, invalid);
        }

        if (auditFailure is not null)
        {
            var invalid = await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, auditFailure.FailureCode, auditFailure.Detail);
            return new RunAdvance(invalid.Run, invalid);
        }

        var cancelled = IsCanonicalCancellationRejection(rejection);
        if (cancelled)
        {
            return await CompleteSequentialCancellationAsync(run, actor, rejection.Detail);
        }

        var failureCode = string.Equals(rejection.Detail, CanonicalDeadlineRejectionDetail, StringComparison.Ordinal)
            ? "run_deadline_exceeded"
            : "canonical_inference_rejected";
        var terminal = await TerminateAsync(run, actor, CustomLoopRunStatus.Failed, failureCode, rejection.Detail);
        return new RunAdvance(terminal.Run, terminal);
    }

    private async Task<RunAdvance> ReconcileSequentialExitRejectionAsync(
        SequentialExecutionContext context,
        CustomLoopRunRecord run,
        CustomLoopRunEvent rejection,
        string actor)
    {
        var auditFailure = await AppendOutcomeAuditAsync(
            run,
            rejection,
            CreateCanonicalRejectionAudit(run, actor, rejection, isExit: true),
            context.AuditRecorder,
            IntegrityToken());
        if (auditFailure is not null)
        {
            var invalid = await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, auditFailure.FailureCode, auditFailure.Detail);
            return new RunAdvance(invalid.Run, invalid);
        }

        var cancelled = IsCanonicalCancellationRejection(rejection);
        if (cancelled)
        {
            return await CompleteSequentialCancellationAsync(run, actor, rejection.Detail);
        }

        var terminal = await TerminateAsync(
            run,
            actor,
            CustomLoopRunStatus.Failed,
            "canonical_exit_rejected",
            rejection.Detail);
        return new RunAdvance(terminal.Run, terminal);
    }

    private static bool IsCanonicalCancellationRejection(CustomLoopRunEvent rejection)
        => string.Equals(rejection.Detail, CanonicalCallerCancellationDetail, StringComparison.Ordinal)
            || string.Equals(rejection.Detail, CanonicalDurableCancellationDetail, StringComparison.Ordinal);

    private async Task<RunAdvance> CompleteSequentialCancellationAsync(CustomLoopRunRecord run, string actor, string detail)
    {
        if (run.Status == CustomLoopRunStatus.CancelRequested)
        {
            var terminal = await TerminateAsync(run, actor, CustomLoopRunStatus.Cancelled, null, detail);
            return new RunAdvance(terminal.Run, terminal);
        }

        var cancelled = await CancelBeforeDispatchAsync(run, actor);
        return new RunAdvance(cancelled.Run, cancelled);
    }

    private async Task<RunAdvance> ObserveControlBoundaryAsync(CustomLoopRunRecord run, string actor)
    {
        var refreshed = await RefreshControlUpdateAsync(run);
        if (refreshed.Terminal is not null)
        {
            return refreshed;
        }

        run = refreshed.Run!;
        if (run.Status == CustomLoopRunStatus.PauseRequested)
        {
            return await PauseAtBoundaryAsync(run, actor);
        }

        if (run.Status == CustomLoopRunStatus.CancelRequested)
        {
            var cancelled = await TerminateAsync(run, actor, CustomLoopRunStatus.Cancelled, null, "Cancellation reached a proved safe checkpoint boundary; no later provider attempt was started.");
            return new RunAdvance(cancelled.Run, cancelled);
        }

        return run.Status == CustomLoopRunStatus.Running
            ? new RunAdvance(run, null)
            : new RunAdvance(null, Result(CustomLoopOrderedRunStatus.InvalidState, run, $"Ordered execution cannot dispatch from {run.Status}."));
    }

    private async Task<RunAdvance> PauseAtBoundaryAsync(CustomLoopRunRecord run, string actor)
    {
        var lastEvent = run.Events.LastOrDefault();
        if (lastEvent is null || lastEvent.Kind != CustomLoopRunEventKind.CheckpointCommitted || run.Checkpoint.LastCommittedSequence != lastEvent.Sequence)
        {
            var boundary = await CommitCheckpointAsync(run, run.Checkpoint, "Pause boundary checkpoint committed before entering Paused.");
            if (boundary.Terminal is not null)
            {
                return boundary;
            }

            run = boundary.Run!;
        }

        var metadata = RunMetadata(run);
        metadata["terminalStatus"] = "paused";
        metadata["checkpointSequence"] = run.Checkpoint.LastCommittedSequence;
        metadata["lifecycleCommitPending"] = true;
        try
        {
            await _auditLog.AppendAsync(AuditEvent.Create(actor, AuditSchema.Actions.LoopRunLifecycle, run.Id, AuditSchema.Outcomes.Succeeded, "A proved checkpoint boundary is ready to enter Paused without another dispatch.", metadata), IntegrityToken());
        }
        catch (Exception exception)
        {
            var terminal = await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, "pause_boundary_audit_failed", $"The pause boundary was proved, but its lifecycle audit failed before Paused could be committed: {SafeExceptionClass(exception)}.");
            return new RunAdvance(terminal.Run, terminal);
        }

        var now = Now(run);
        var lifecycle = Event(run, now, CustomLoopRunEventKind.LifecycleChanged, "The run entered Paused at a proved checkpoint boundary; Resume is required for any later dispatch.");
        var candidate = run with
        {
            LifecycleVersion = run.LifecycleVersion + 1,
            Status = CustomLoopRunStatus.Paused,
            UpdatedAtUtc = now,
            ExecutionClock = AdvanceClock(run.ExecutionClock, now, terminal: true),
            Events = [.. run.Events, lifecycle]
        };
        var persisted = await PersistAsync(run, candidate, IntegrityToken(), outcomeMayExist: false);
        if (persisted.Terminal is not null)
        {
            return persisted;
        }

        return new RunAdvance(persisted.Run, Result(CustomLoopOrderedRunStatus.Paused, persisted.Run, "The run is Paused at a committed checkpoint; no later attempt was dispatched."));
    }

    private async Task<RunAdvance> RefreshControlUpdateAsync(CustomLoopRunRecord run)
    {
        CustomLoopRunRecord? latest;
        try
        {
            latest = await _runStore.GetAsync(run.Id, IntegrityToken());
        }
        catch (Exception exception)
        {
            return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.NeedsReview, run, $"The durable run could not be refreshed after provider dispatch: {SafeExceptionClass(exception)}."));
        }

        if (latest is null)
        {
            return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.NotFound, null, "The run trace disappeared during ordered execution."));
        }

        if (latest.LifecycleVersion == run.LifecycleVersion)
        {
            return new RunAdvance(run, null);
        }

        var validation = CustomLoopRunValidator.Validate(latest);
        if (!validation.IsValid || !IsAcceptedControlSuccessor(run, latest))
        {
            return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.Conflict, latest, "The run changed outside the accepted pause/cancel control protocol; no automatic replay or later dispatch was attempted."));
        }

        return new RunAdvance(latest, null);
    }

    private static bool IsAcceptedControlSuccessor(CustomLoopRunRecord current, CustomLoopRunRecord latest)
    {
        var acceptedStatus = latest.Status == current.Status
            || latest.Status == CustomLoopRunStatus.CancelRequested
            || current.Status == CustomLoopRunStatus.Running && latest.Status == CustomLoopRunStatus.PauseRequested;
        if (!acceptedStatus || latest.LifecycleVersion <= current.LifecycleVersion || latest.Events.Length <= current.Events.Length || !CheckpointsEqual(current.Checkpoint, latest.Checkpoint))
        {
            return false;
        }

        for (var index = 0; index < current.Events.Length; index++)
        {
            if (!string.Equals(current.Events[index].EventId, latest.Events[index].EventId, StringComparison.Ordinal))
            {
                return false;
            }
        }

        var appended = latest.Events.Skip(current.Events.Length).ToArray();
        // Concurrent successors may append governed tool evidence and a lifecycle request only.
        // Any checkpoint mutation or other execution event could hide a second traversal and is rejected.
        var supported = appended.All(item => item.Kind is CustomLoopRunEventKind.LifecycleChanged
            or CustomLoopRunEventKind.ToolRequestReserved
            or CustomLoopRunEventKind.ToolGovernanceDecided
            or CustomLoopRunEventKind.ToolOutcomeObserved
            or CustomLoopRunEventKind.ToolIntegrityFailed);
        var hasControl = appended.Any(item => item.Kind == CustomLoopRunEventKind.LifecycleChanged);
        return supported && (latest.Status == current.Status || hasControl);
    }

    private static bool CheckpointsEqual(CustomLoopRunCheckpoint left, CustomLoopRunCheckpoint right)
    {
        return left.Iteration == right.Iteration
            && left.NextStepIndex == right.NextStepIndex
            && left.AcceptedRepeatCount == right.AcceptedRepeatCount
            && left.PendingExitDecision == right.PendingExitDecision
            && left.ToolRequestsUsed == right.ToolRequestsUsed
            && left.LastCommittedSequence == right.LastCommittedSequence
            && left.EarlierRetainedOutputs.SequenceEqual(right.EarlierRetainedOutputs)
            && Equals(left.PreviousIterationResult, right.PreviousIterationResult)
            && Equals(left.CurrentIterationResult, right.CurrentIterationResult);
    }

    private async Task<CustomLoopOrderedRunResult> CancelBeforeDispatchAsync(CustomLoopRunRecord run, string actor)
    {
        var integrity = IntegrityToken();
        if (run.Status == CustomLoopRunStatus.Admitted)
        {
            return await TerminateAsync(run, actor, CustomLoopRunStatus.Cancelled, null, "Caller cancellation was observed before any provider dispatch.");
        }

        var requestedNow = Now(run);
        var requestedEvent = Event(run, requestedNow, CustomLoopRunEventKind.LifecycleChanged, "Cancellation was requested at a proved safe dispatch boundary.");
        var requestedCandidate = run with
        {
            LifecycleVersion = run.LifecycleVersion + 1,
            Status = CustomLoopRunStatus.CancelRequested,
            UpdatedAtUtc = requestedNow,
            ExecutionClock = AdvanceClock(run.ExecutionClock, requestedNow, terminal: false),
            Events = [.. run.Events, requestedEvent]
        };
        var requested = await PersistAsync(run, requestedCandidate, integrity, outcomeMayExist: false);
        if (requested.Terminal is not null)
        {
            return requested.Terminal;
        }

        try
        {
            await _auditLog.AppendAsync(AuditEvent.Create(actor, AuditSchema.Actions.LoopRunLifecycle, run.Id, AuditSchema.Outcomes.Requested, "Custom-loop cancellation requested at a safe boundary.", RunMetadata(requested.Run!)), integrity);
        }
        catch
        {
            // Cancellation remains safe because no provider request or actuator is open.
        }

        return await TerminateAsync(requested.Run!, actor, CustomLoopRunStatus.Cancelled, null, "Custom-loop execution was cancelled at a proved safe boundary.");
    }

    private async Task<CustomLoopOrderedRunResult> TerminateAsync(CustomLoopRunRecord run, string actor, CustomLoopRunStatus status, string? failureCode, string detail, string? finalOutput = null)
    {
        var now = Now(run);
        var terminalEvent = Event(run, now, CustomLoopRunEventKind.LifecycleChanged, detail);
        var candidate = run with
        {
            LifecycleVersion = run.LifecycleVersion + 1,
            Status = status,
            UpdatedAtUtc = now,
            CompletedAtUtc = now,
            ExecutionClock = AdvanceClock(run.ExecutionClock, now, terminal: true),
            Events = [.. run.Events, terminalEvent],
            FinalOutput = status == CustomLoopRunStatus.Completed ? finalOutput ?? string.Empty : null,
            FailureCode = status is CustomLoopRunStatus.Failed or CustomLoopRunStatus.NeedsReview ? failureCode : null,
            FailureDetail = status is CustomLoopRunStatus.Failed or CustomLoopRunStatus.NeedsReview ? detail : null
        };
        var persisted = await PersistAsync(run, candidate, IntegrityToken(), outcomeMayExist: true);
        if (persisted.Run is null)
        {
            return persisted.Terminal ?? Result(CustomLoopOrderedRunStatus.NeedsReview, run, "The terminal trace could not be committed safely.");
        }

        var terminalRun = persisted.Run;
        var resultStatus = status switch
        {
            CustomLoopRunStatus.Completed => CustomLoopOrderedRunStatus.Completed,
            CustomLoopRunStatus.Cancelled => CustomLoopOrderedRunStatus.Cancelled,
            CustomLoopRunStatus.Failed => CustomLoopOrderedRunStatus.Failed,
            CustomLoopRunStatus.NeedsReview => CustomLoopOrderedRunStatus.NeedsReview,
            _ => CustomLoopOrderedRunStatus.InvalidState
        };

        var terminalMetadata = RunMetadata(terminalRun);
        terminalMetadata["terminalStatus"] = status.ToString().ToLowerInvariant();
        terminalMetadata["failureCode"] = failureCode;
        terminalMetadata["lifecycleCommitPending"] = false;
        terminalMetadata["terminalTraceSequence"] = terminalEvent.Sequence;
        try
        {
            // The terminal trace is already the source of truth. Audit failure cannot roll it back;
            // the fallback appends an integrity warning while preserving the truthful terminal status.
            var auditOutcome = status switch
            {
                CustomLoopRunStatus.Failed => AuditSchema.Outcomes.Failed,
                CustomLoopRunStatus.NeedsReview => AuditSchema.Outcomes.NeedsReview,
                _ => AuditSchema.Outcomes.Succeeded
            };
            await _auditLog.AppendAsync(AuditEvent.Create(actor, AuditSchema.Actions.LoopRunLifecycle, terminalRun.Id, auditOutcome, "Terminal lifecycle trace is durable.", terminalMetadata), IntegrityToken());
            return Result(resultStatus, terminalRun, detail);
        }
        catch (Exception exception)
        {
            var warningDetail = $"The truthful {status} terminal trace is durable, but its terminal audit append failed: {SafeExceptionClass(exception)}.";
            var warning = Event(terminalRun, Now(terminalRun), CustomLoopRunEventKind.IntegrityWarning, warningDetail);
            try
            {
                var warningPersisted = await _runStore.AppendTerminalIntegrityWarningAsync(terminalRun.Id, terminalRun.LifecycleVersion, warning, IntegrityToken());
                if (warningPersisted.Status == CustomLoopRunStoreStatus.Updated && warningPersisted.Run is not null)
                {
                    return Result(resultStatus, warningPersisted.Run, warningDetail);
                }

                return Result(resultStatus, terminalRun, $"{warningDetail} The post-terminal integrity warning could not be durably appended ({warningPersisted.Status}).");
            }
            catch (Exception warningException)
            {
                return Result(resultStatus, terminalRun, $"{warningDetail} The post-terminal integrity warning persistence outcome is uncertain: {SafeExceptionClass(warningException)}.");
            }
        }
    }

    private async Task<RunAdvance> PersistAsync(CustomLoopRunRecord current, CustomLoopRunRecord candidate, CancellationToken cancellationToken, bool outcomeMayExist, bool propagateCancellation = false)
    {
        // Before an external effect, persistence failure is a definite stop. After a provider or
        // publication may have acted, the same failure must park the run in NeedsReview to forbid replay.
        try
        {
            var result = await _runStore.UpdateAsync(candidate, current.LifecycleVersion, cancellationToken);
            if (result.Status == CustomLoopRunStoreStatus.Updated && result.Run is not null)
            {
                return new RunAdvance(result.Run, null);
            }

            if (result.Status is CustomLoopRunStoreStatus.Conflict or CustomLoopRunStoreStatus.TerminalImmutable)
            {
                return outcomeMayExist
                    ? await EscalatePostOutcomePersistenceUncertaintyAsync(current, "An external outcome may exist, but its required trace update conflicted with concurrent lifecycle state. Human review is required before resume.")
                    : new RunAdvance(null, Result(CustomLoopOrderedRunStatus.Conflict, null, "The run changed concurrently; no automatic replay was attempted."));
            }

            return result.Status switch
            {
                CustomLoopRunStoreStatus.NotFound => new RunAdvance(null, Result(CustomLoopOrderedRunStatus.NotFound, null, "The run trace disappeared during execution.")),
                _ => new RunAdvance(null, Result(outcomeMayExist ? CustomLoopOrderedRunStatus.NeedsReview : CustomLoopOrderedRunStatus.Failed, current, "The required run-trace update was rejected; no later attempt was started."))
            };
        }
        catch (OperationCanceledException) when (propagateCancellation && cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (UnsupportedCustomLoopRunDiscoveryIndexSchemaException exception) when (outcomeMayExist)
        {
            return await EscalatePostOutcomePersistenceUncertaintyAsync(current, $"{exception.Message} An external outcome may exist, but its required trace update could not be committed. Human review is required before resume.");
        }
        catch (UnsupportedCustomLoopRunDiscoveryIndexSchemaException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return outcomeMayExist
                ? await EscalatePostOutcomePersistenceUncertaintyAsync(current, "An external outcome may exist, but its required trace update timed out. Human review is required before resume.")
                : new RunAdvance(null, Result(CustomLoopOrderedRunStatus.Failed, current, "The required pre-effect run-trace update timed out; no later attempt was started."));
        }
        catch (Exception exception)
        {
            return outcomeMayExist
                ? await EscalatePostOutcomePersistenceUncertaintyAsync(current, $"An external outcome may exist, but its required trace update failed with {SafeExceptionClass(exception)}. Human review is required before resume.")
                : new RunAdvance(null, Result(CustomLoopOrderedRunStatus.Failed, current, $"The required run-trace update failed: {SafeExceptionClass(exception)}. No later attempt was started."));
        }
    }

    private async Task<RunAdvance> EscalatePostOutcomePersistenceUncertaintyAsync(CustomLoopRunRecord current, string detail)
    {
        const string FailureCode = "post_outcome_persistence_conflict";
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var latest = await _runStore.GetAsync(current.Id, IntegrityToken());
                if (latest is null)
                {
                    return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.NeedsReview, current, $"{detail} The latest run trace could not be found."));
                }

                if (latest.Status == CustomLoopRunStatus.NeedsReview)
                {
                    return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.NeedsReview, latest, detail));
                }

                if (latest.IsTerminal)
                {
                    var warning = Event(latest, Now(latest), CustomLoopRunEventKind.IntegrityWarning, detail);
                    var warningPersisted = await _runStore.AppendTerminalIntegrityWarningAsync(latest.Id, latest.LifecycleVersion, warning, IntegrityToken());
                    var durable = warningPersisted.Status == CustomLoopRunStoreStatus.Updated && warningPersisted.Run is not null ? warningPersisted.Run : latest;
                    var warningDetail = warningPersisted.Status == CustomLoopRunStoreStatus.Updated
                        ? detail
                        : $"{detail} The concurrent terminal trace could not accept its integrity warning ({warningPersisted.Status}).";
                    return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.NeedsReview, durable, warningDetail));
                }

                var now = Now(latest);
                var lifecycle = Event(latest, now, CustomLoopRunEventKind.LifecycleChanged, detail);
                var needsReview = latest with
                {
                    LifecycleVersion = latest.LifecycleVersion + 1,
                    Status = CustomLoopRunStatus.NeedsReview,
                    UpdatedAtUtc = now,
                    CompletedAtUtc = now,
                    ExecutionClock = AdvanceClock(latest.ExecutionClock, now, terminal: true),
                    Events = [.. latest.Events, lifecycle],
                    FinalOutput = null,
                    FailureCode = FailureCode,
                    FailureDetail = detail
                };
                var persisted = await _runStore.UpdateAsync(needsReview, latest.LifecycleVersion, IntegrityToken());
                if (persisted.Status == CustomLoopRunStoreStatus.Updated && persisted.Run is not null)
                {
                    return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.NeedsReview, persisted.Run, detail));
                }

                if (persisted.Status == CustomLoopRunStoreStatus.NotFound)
                {
                    return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.NeedsReview, latest, $"{detail} The run trace disappeared during escalation."));
                }
            }
        }
        catch (UnsupportedCustomLoopRunDiscoveryIndexSchemaException exception)
        {
            throw new UnsupportedCustomLoopRunDiscoveryIndexSchemaException(exception.SchemaVersion, $"{detail} The NeedsReview escalation could not be persisted because the run discovery index still has an unsupported schema.", exception);
        }
        catch (Exception exception)
        {
            return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.NeedsReview, current, $"{detail} Escalation persistence is uncertain: {SafeExceptionClass(exception)}."));
        }

        return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.NeedsReview, current, $"{detail} Concurrent updates prevented the bounded escalation write."));
    }

    private static CustomLoopRunRecord Append(CustomLoopRunRecord run, DateTimeOffset now, IReadOnlyList<CustomLoopRunEvent> events)
    {
        return run with
        {
            LifecycleVersion = run.LifecycleVersion + 1,
            UpdatedAtUtc = now,
            Events = [.. run.Events, .. events]
        };
    }

    private CustomLoopRunEvent Event(
        CustomLoopRunRecord run,
        DateTimeOffset now,
        CustomLoopRunEventKind kind,
        string detail,
        int? iteration = null,
        string? stepId = null,
        int? attempt = null,
        CustomLoopContextBlock[]? contextBlocks = null,
        string? output = null,
        int? originalOutputCharacters = null,
        bool? truncated = null,
        bool? retained = null,
        bool? published = null,
        string? publicationId = null,
        string? provider = null,
        string? model = null,
        string? providerResponseId = null,
        CustomLoopExitDecision? exitDecision = null,
        CustomLoopToolAuthoritySnapshot? toolAuthority = null,
        CustomLoopToolTraceEvidence? toolEvidence = null,
        int? traceReservationUtf8Bytes = null)
    {
        return new CustomLoopRunEvent(run.Events.Length + 1, NewCorrelationId("event"), now, kind, iteration, stepId, attempt, detail, contextBlocks ?? [], output, originalOutputCharacters, truncated, retained, published, publicationId, provider, model, providerResponseId, exitDecision, toolAuthority, toolEvidence, traceReservationUtf8Bytes);
    }

    private static CustomLoopRunEvent WithSequentialEvidence(
        CustomLoopRunEvent runEvent,
        SequentialNodeExecutionContext context,
        CustomLoopSequentialNodeEvidenceKind kind,
        CustomLoopSequentialNodeDisposition disposition)
    {
        var binding = context.Binding;
        var evidence = CustomLoopSequentialNodeEvidenceHash.Apply(new CustomLoopSequentialNodeEvidence(
            CustomLoopSequentialNodeEvidence.CurrentSchemaVersion,
            kind,
            binding.WorkspaceId,
            binding.ExecutionBinding.RunId,
            binding.ExecutionBinding.Revision,
            binding.ExecutionBinding.ExecutionGeneration,
            context.Node.NodeId,
            context.Attempt,
            disposition,
            CustomLoopSequentialOutcomeArtifactHash.Compute(runEvent),
            string.Empty));
        return runEvent with { SequentialNodeEvidence = evidence };
    }

    private static AuditEvent CreateDeterministicExitAudit(
        CustomLoopRunRecord run,
        string actor,
        string detail,
        CustomLoopRunEvent completed)
    {
        var retained = run.Events.SingleOrDefault(item => string.Equals(item.EventId, completed.EventId, StringComparison.Ordinal));
        var sequentialEvidence = retained?.SequentialNodeEvidence ?? completed.SequentialNodeEvidence;
        var metadata = RunMetadata(run);
        metadata["iteration"] = run.Checkpoint.Iteration;
        metadata["decision"] = "complete";
        metadata["modelDispatched"] = false;
        metadata["canonicalNodeId"] = sequentialEvidence?.NodeId;
        metadata["sequentialEvidenceHash"] = sequentialEvidence?.EvidenceHash;
        return new AuditEvent(
            completed.TimestampUtc.ToUniversalTime(),
            actor,
            AuditSchema.Actions.LoopExitDecision,
            run.Id,
            AuditSchema.Outcomes.Succeeded,
            detail,
            metadata);
    }

    private static AuditEvent CreateCanonicalRejectionAudit(
        CustomLoopRunRecord run,
        string actor,
        CustomLoopRunEvent rejection,
        bool isExit)
    {
        var evidence = rejection.SequentialNodeEvidence
            ?? throw new InvalidOperationException("Canonical rejection audit requires terminal sequential evidence.");
        var metadata = RunMetadata(run);
        metadata["iteration"] = rejection.Iteration;
        metadata["stepId"] = rejection.StepId;
        metadata["attempt"] = rejection.Attempt;
        metadata["attemptCorrelationId"] = rejection.ProviderResponseId;
        metadata["provider"] = run.ModelSnapshot.Provider;
        metadata["model"] = run.ModelSnapshot.Model;
        metadata["canonicalNodeId"] = evidence.NodeId;
        metadata["sequentialEvidenceHash"] = evidence.EvidenceHash;
        return new AuditEvent(
            rejection.TimestampUtc.ToUniversalTime(),
            actor,
            isExit ? AuditSchema.Actions.LoopExitDecision : AuditSchema.Actions.LoopNodeAttempt,
            run.Id,
            AuditSchema.Outcomes.Failed,
            "Canonical node dispatch was rejected before provider invocation.",
            metadata);
    }

    private async Task<SequentialAuditBoundaryFailure?> AppendOutcomeAuditAsync(
        CustomLoopRunRecord run,
        CustomLoopRunEvent terminalEvent,
        AuditEvent auditEvent,
        IGovernedLoopSequentialAuditRecorder? sequentialAuditRecorder,
        CancellationToken cancellationToken)
    {
        if (sequentialAuditRecorder is null)
        {
            await _auditLog.AppendAsync(auditEvent, cancellationToken);
            return null;
        }

        var evidence = terminalEvent.SequentialNodeEvidence;
        if (evidence is null
            || evidence.Kind == CustomLoopSequentialNodeEvidenceKind.DispatchStarted
            || evidence.Disposition == CustomLoopSequentialNodeDisposition.Unknown
            || !CustomLoopSequentialNodeEvidenceHash.Matches(evidence)
            || !CustomLoopSequentialOutcomeArtifactHash.Matches(terminalEvent))
        {
            return new SequentialAuditBoundaryFailure(
                "canonical_outcome_audit_conflict",
                "The terminal canonical node evidence could not identify one exact append-once audit operation.");
        }

        GovernedLoopSequentialAuditRecordResult? recorded;
        try
        {
            recorded = await sequentialAuditRecorder.RecordOnceAsync(
                GovernedLoopSequentialAuditOperationId.ForNodeOutcome(evidence.EvidenceHash),
                evidence.EvidenceHash,
                auditEvent with
                {
                    TimestampUtc = terminalEvent.TimestampUtc.ToUniversalTime(),
                    Actor = run.AdmissionActor,
                },
                cancellationToken);
        }
        catch (Exception exception)
        {
            return new SequentialAuditBoundaryFailure(
                "canonical_outcome_audit_unavailable",
                $"The append-once canonical node audit could not prove durability: {SafeExceptionClass(exception)}.");
        }

        return recorded?.Status switch
        {
            GovernedLoopSequentialAuditRecordStatus.Recorded or GovernedLoopSequentialAuditRecordStatus.AlreadyRecorded => null,
            GovernedLoopSequentialAuditRecordStatus.Conflict => new SequentialAuditBoundaryFailure(
                "canonical_outcome_audit_conflict",
                "The append-once canonical node audit operation is already bound to divergent evidence."),
            _ => new SequentialAuditBoundaryFailure(
                "canonical_outcome_audit_unavailable",
                "The append-once canonical node audit could not prove a durable outcome."),
        };
    }

    private static AuditEvent AttemptAudit(
        string actor,
        CustomLoopRunRecord run,
        string stepId,
        int iteration,
        string correlation,
        CustomLoopContextAssembly assembly,
        string action,
        string outcome,
        CanonicalOutput? canonical,
        CustomLoopInferenceAttemptResult? result,
        CustomLoopExitDecision? exitDecision = null,
        int attempt = 1)
    {
        var metadata = RunMetadata(run);
        metadata["iteration"] = iteration;
        metadata["stepId"] = stepId;
        metadata["attempt"] = attempt;
        metadata["attemptCorrelationId"] = correlation;
        metadata["provider"] = run.ModelSnapshot.Provider;
        metadata["model"] = run.ModelSnapshot.Model;
        metadata["providerResponseId"] = SafeReference(result?.ProviderResponseId);
        metadata["logicalRequestCharacters"] = assembly.LogicalRequestCharacterCount;
        metadata["contextBlockCount"] = assembly.Blocks.Length;
        metadata["outputCharacters"] = canonical?.Text.Length;
        metadata["originalOutputCharacters"] = canonical?.OriginalCharacterCount;
        metadata["outputHash"] = canonical is null ? null : CustomLoopTraceContentHash.Compute(canonical.Text);
        metadata["outputTruncated"] = canonical?.Truncated;
        metadata["exitDecision"] = exitDecision?.ToString().ToLowerInvariant();
        metadata["toolRequestsConsumed"] = result?.ToolRequestsConsumed;
        var authority = run.Events.LastOrDefault(item => item.Kind is CustomLoopRunEventKind.NodeAttemptStarted or CustomLoopRunEventKind.ExitDecisionStarted
            && item.Iteration == iteration
            && string.Equals(item.StepId, stepId, StringComparison.Ordinal)
            && item.Attempt == attempt)?.ToolAuthority;
        metadata["admittedCommands"] = authority is null ? null : string.Join(',', authority.AdmittedMaximum.OrderBy(value => value));
        metadata["currentRoleCommands"] = authority is null ? null : string.Join(',', authority.CurrentRoleCeiling.OrderBy(value => value));
        metadata["effectiveCommands"] = authority is null ? null : string.Join(',', authority.EffectiveAssignments.OrderBy(value => value));
        metadata["roleCeilingHash"] = authority?.RoleCeilingHash;
        metadata["catalogHash"] = authority?.CatalogHash;
        var sequentialEvidence = run.Events.LastOrDefault(item => item.Iteration == iteration
            && string.Equals(item.StepId, stepId, StringComparison.Ordinal)
            && item.Attempt == attempt
            && item.SequentialNodeEvidence is { Kind: not CustomLoopSequentialNodeEvidenceKind.DispatchStarted })?.SequentialNodeEvidence;
        metadata["canonicalNodeId"] = sequentialEvidence?.NodeId;
        metadata["sequentialEvidenceHash"] = sequentialEvidence?.EvidenceHash;
        return AuditEvent.Create(actor, action, run.Id, outcome, outcome == AuditSchema.Outcomes.Started ? "Model attempt is safe to dispatch after matching trace and audit persistence." : "Model attempt outcome metadata was recorded without raw prompt or response content.", metadata);
    }

    private static Dictionary<string, object?> RunMetadata(CustomLoopRunRecord run)
    {
        return new Dictionary<string, object?>
        {
            ["runId"] = run.Id,
            ["loopId"] = run.LoopId,
            ["roleId"] = run.AdmittedDefinition.RoleId,
            ["definitionVersion"] = run.AdmittedDefinition.DefinitionVersion,
            ["definitionHash"] = run.AdmittedDefinition.ContentHash,
            ["surface"] = run.Surface
        };
    }

    private static string? ValidateProviderResult(CustomLoopRunRecord run, CustomLoopInferenceAttemptResult result, int iteration, string stepId, int attempt, out int durableToolRequestsConsumed)
    {
        durableToolRequestsConsumed = 0;
        if (result is null)
        {
            return "The provider executor returned no result after dispatch.";
        }

        if (!string.Equals(result.Provider, run.ModelSnapshot.Provider, StringComparison.Ordinal) || !string.Equals(result.Model, run.ModelSnapshot.Model, StringComparison.Ordinal))
        {
            return "The provider/model result does not match the immutable admitted model snapshot.";
        }

        if (result.ToolRequestsConsumed < 0 || result.ToolRequestsConsumed > CustomLoopLimits.MaxModelVisibleGovernedToolRequestsPerAttempt)
        {
            return "The provider result reported a governed tool-call count outside the admitted per-attempt budget.";
        }

        var attemptStart = run.Events.LastOrDefault(item => (item.Kind is CustomLoopRunEventKind.NodeAttemptStarted or CustomLoopRunEventKind.ExitDecisionStarted)
            && item.Iteration == iteration
            && string.Equals(item.StepId, stepId, StringComparison.Ordinal)
            && item.Attempt == attempt);
        if (attemptStart?.ToolAuthority is null)
        {
            return "The durable governed tool trace has no matching attempt-start authority snapshot.";
        }

        var toolEvents = run.Events
            .Where(item => item.Sequence > attemptStart.Sequence && item.Iteration == iteration && item.Attempt == attempt && string.Equals(item.StepId, stepId, StringComparison.Ordinal) && item.ToolEvidence is not null)
            .ToArray();
        if (toolEvents.Any(item => item.ToolAuthority is null
            || item.ToolEvidence is null
            || !item.ToolAuthority.IsBoundedRefreshOf(attemptStart.ToolAuthority)
            || !item.ToolEvidence.Authority.IsBoundedRefreshOf(attemptStart.ToolAuthority)
            || !attemptStart.ToolAuthority.AllowsCommand(item.ToolEvidence.Command)))
        {
            return "The durable governed tool trace is not bound to a fresh non-widening authority refresh and the attempt-start allowed command set.";
        }

        var reservations = toolEvents.Where(item => item.Kind == CustomLoopRunEventKind.ToolRequestReserved).ToArray();
        durableToolRequestsConsumed = reservations.Length;
        if (durableToolRequestsConsumed > CustomLoopLimits.MaxModelVisibleGovernedToolRequestsPerAttempt || run.Checkpoint.ToolRequestsUsed + durableToolRequestsConsumed > CustomLoopLimits.MaxModelVisibleGovernedToolRequestsPerRun)
        {
            return "The durable governed tool-request trace exceeds the admitted run budget.";
        }

        var ordinals = reservations.Select(item => item.ToolEvidence!.RequestOrdinal).OrderBy(value => value).ToArray();
        if (!ordinals.SequenceEqual(Enumerable.Range(1, durableToolRequestsConsumed)))
        {
            return "The durable governed tool-request reservations do not have unique contiguous ordinals.";
        }

        var groups = toolEvents.GroupBy(item => (item.ToolEvidence!.RequestOrdinal, item.ToolEvidence.RequestCorrelationId)).ToArray();
        var reservedGroups = groups.Where(group => group.Any(item => item.Kind == CustomLoopRunEventKind.ToolRequestReserved)).ToArray();
        var unreservedGroups = groups.Where(group => group.All(item => item.Kind != CustomLoopRunEventKind.ToolRequestReserved)).ToArray();
        if (reservedGroups.Length != durableToolRequestsConsumed
            || unreservedGroups.Length > 1
            || unreservedGroups.Any(group => group.Count() != 1
                || group.Single().Kind != CustomLoopRunEventKind.ToolIntegrityFailed
                || group.Key.RequestOrdinal != reservations.Length + 1))
        {
            return "The durable governed tool trace contains a duplicate reservation or evidence outside the one exact repeated-request integrity slot.";
        }

        if (unreservedGroups.Length == 1)
        {
            return "A repeated governed tool request recorded an exact non-actuating integrity failure and cannot be counted as a completed request.";
        }

        foreach (var group in reservedGroups)
        {
            var ordered = group.OrderBy(item => item.Sequence).ToArray();
            if (ordered.Count(item => item.Kind == CustomLoopRunEventKind.ToolIntegrityFailed) > 0)
            {
                return "A governed tool request recorded an integrity failure and cannot be counted as a completed request.";
            }

            if (ordered.Length != 4
                || ordered[0].Kind != CustomLoopRunEventKind.ToolRequestReserved
                || ordered[1].Kind != CustomLoopRunEventKind.ToolGovernanceDecided
                || ordered[2].Kind != CustomLoopRunEventKind.ToolOutcomeObserved
                || ordered[3].Kind != CustomLoopRunEventKind.ToolOutcomeObserved
                || ordered[2].ToolEvidence!.ReturnedToModel
                || !ordered[3].ToolEvidence!.ReturnedToModel
                || !ToolOutcomeEvidenceMatches(ordered[2].ToolEvidence!, ordered[3].ToolEvidence!)
                || ordered.Select(item => item.ToolEvidence!.RequestOrdinal).Distinct().Count() != 1)
            {
                return "Each durable governed tool request must have one ordered reservation, governance decision, observed outcome, and exact returned-to-model marker before attempt completion.";
            }
        }

        if (result.ToolRequestsConsumed != durableToolRequestsConsumed)
        {
            return $"The provider result reported {result.ToolRequestsConsumed} governed tool requests, but the durable completed trace records {durableToolRequestsConsumed}.";
        }

        return null;
    }

    private static bool ToolOutcomeEvidenceMatches(CustomLoopToolTraceEvidence observed, CustomLoopToolTraceEvidence returned)
    {
        return observed.Phase == CustomLoopToolEvidencePhase.OutcomeObserved
            && returned.Phase == CustomLoopToolEvidencePhase.OutcomeObserved
            && observed.RequestOrdinal == returned.RequestOrdinal
            && string.Equals(observed.RequestCorrelationId, returned.RequestCorrelationId, StringComparison.Ordinal)
            && string.Equals(observed.BrokerRequestId, returned.BrokerRequestId, StringComparison.Ordinal)
            && observed.Command == returned.Command
            && string.Equals(observed.TargetPath, returned.TargetPath, StringComparison.Ordinal)
            && string.Equals(observed.Content, returned.Content, StringComparison.Ordinal)
            && string.Equals(observed.Pattern, returned.Pattern, StringComparison.Ordinal)
            && string.Equals(observed.ResolvedTarget, returned.ResolvedTarget, StringComparison.Ordinal)
            && observed.Authority.Matches(returned.Authority)
            && Equals(observed.Governance, returned.Governance)
            && observed.Outcome == returned.Outcome
            && string.Equals(observed.CanonicalResultReturnedToModel, returned.CanonicalResultReturnedToModel, StringComparison.Ordinal)
            && string.Equals(observed.CanonicalResultHash, returned.CanonicalResultHash, StringComparison.Ordinal)
            && observed.CanonicalResultCharacterCount == returned.CanonicalResultCharacterCount
            && observed.ReservedUtf8Bytes == returned.ReservedUtf8Bytes;
    }

    private static void EnsureRequestBound(CustomLoopContextAssembly assembly)
    {
        if (assembly.LogicalRequestCharacterCount > CustomLoopLimits.MaxLogicalProviderRequestCharacters)
        {
            throw new InvalidOperationException("The assembled logical provider request exceeds the server-owned character limit.");
        }
    }

    private static void EnsureAttemptBound(CustomLoopRunRecord run)
    {
        var startedAttempts = run.Events.Count(start => (start.Kind is CustomLoopRunEventKind.NodeAttemptStarted or CustomLoopRunEventKind.ExitDecisionStarted) && AttemptConsumesBudget(run, start));
        var definitionMaximum = CustomLoopLimits.GetMaximumModelAttempts(run.AdmittedDefinition.InferenceSteps.Length, run.AdmittedDefinition.ExitPolicy.MaxAdditionalIterations);
        if (startedAttempts >= definitionMaximum || startedAttempts >= CustomLoopLimits.MaxModelAttemptsPerRun)
        {
            throw new InvalidOperationException("The custom-loop model-attempt limit has been reached.");
        }
    }

    private static void EnsureAuthorityBound(CustomLoopRunRecord run, CustomLoopToolAuthoritySnapshot authority, IReadOnlyList<CustomLoopToolAssignment> admittedMaximum)
    {
        if (!authority.IsValid)
        {
            throw new InvalidOperationException(authority.Detail);
        }

        if (!string.Equals(authority.RoleId, run.AdmittedDefinition.RoleId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The resolved tool-authority snapshot belongs to a different role than the immutable admission snapshot.");
        }

        if (!AssignmentSetsEqual(authority.AdmittedMaximum, admittedMaximum) || !authority.EffectiveAssignments.All(admittedMaximum.Contains))
        {
            throw new InvalidOperationException("The resolved tool-authority snapshot is not bounded by the immutable admitted tool assignments.");
        }
    }

    private CustomLoopToolAuthoritySnapshot CanonicalToolFreeAuthority(CustomLoopRunRecord run)
    {
        if (run.SequentialAdapterBinding is null || run.AdmittedDefinition.ToolAssignments.Length != 0)
        {
            throw new InvalidOperationException("Canonical sequential execution requires the exact admitted tool-free projection.");
        }

        return new CustomLoopToolAuthoritySnapshot(
            run.AdmittedDefinition.RoleId,
            [],
            [],
            [],
            [],
            CustomLoopTraceContentHash.Compute($"canonical-sequential-role-v1\n{run.SequentialAdapterBinding.ContentHash}\n{run.AdmittedDefinition.RoleId}"),
            CustomLoopTraceContentHash.Compute("canonical-sequential-empty-tool-catalog-v1"),
            Now(run),
            true,
            "The exact admitted canonical sequential projection is tool-free; no mutable role or tool catalog was resolved.");
    }

    private static bool AssignmentSetsEqual(IReadOnlyList<CustomLoopToolAssignment> left, IReadOnlyList<CustomLoopToolAssignment> right)
    {
        return left.Count == right.Count && left.OrderBy(value => value).SequenceEqual(right.OrderBy(value => value));
    }

    private static bool AttemptConsumesBudget(CustomLoopRunRecord run, CustomLoopRunEvent start)
    {
        if (start.SequentialNodeEvidence is
            {
                Kind: CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
            } sequentialStart
            && run.Events.Any(item => item.Sequence > start.Sequence
                && string.Equals(item.Detail, CanonicalPauseRejectionDetail, StringComparison.Ordinal)
                && item.SequentialNodeEvidence is
                {
                    Kind: CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection,
                } rejection
                && string.Equals(rejection.NodeId, sequentialStart.NodeId, StringComparison.Ordinal)
                && rejection.Attempt == sequentialStart.Attempt))
        {
            return false;
        }

        if (start.Sequence > run.Checkpoint.LastCommittedSequence)
        {
            return true;
        }

        var nextMatchingStart = run.Events.FirstOrDefault(item => item.Sequence > start.Sequence && item.Kind == start.Kind && AttemptCoordinatesEqual(item, start));
        var endSequence = nextMatchingStart?.Sequence ?? long.MaxValue;
        return run.Events.Any(item => item.Sequence > start.Sequence && item.Sequence < endSequence && AttemptCoordinatesEqual(item, start) && CompletesAttempt(start, item));
    }

    private static bool AttemptCoordinatesEqual(CustomLoopRunEvent left, CustomLoopRunEvent right) => left.Iteration == right.Iteration && string.Equals(left.StepId, right.StepId, StringComparison.Ordinal) && left.Attempt == right.Attempt;

    private static bool CompletesAttempt(CustomLoopRunEvent start, CustomLoopRunEvent item) => item.Kind == CustomLoopRunEventKind.NodeAttemptFailed || start.Kind == CustomLoopRunEventKind.NodeAttemptStarted && item.Kind == CustomLoopRunEventKind.NodeAttemptCompleted || start.Kind == CustomLoopRunEventKind.ExitDecisionStarted && item.Kind == CustomLoopRunEventKind.ExitDecisionCompleted;

    private static bool HasCommittedExitCompletion(CustomLoopRunRecord run)
    {
        return !run.Checkpoint.PendingExitDecision
            && run.Checkpoint.NextStepIndex == run.AdmittedDefinition.InferenceSteps.Length
            && run.Checkpoint.CurrentIterationResult is not null
            && run.Events.Any(item => item.Sequence <= run.Checkpoint.LastCommittedSequence
                && item.Kind == CustomLoopRunEventKind.ExitDecisionCompleted
                && item.Iteration == run.Checkpoint.Iteration
                && string.Equals(item.StepId, "exit", StringComparison.Ordinal)
                && item.ExitDecision == CustomLoopExitDecision.Complete);
    }

    private async Task<RunAdvance?> RejectUnavailableTraceCapacityAsync(CustomLoopRunRecord current, CustomLoopRunRecord candidate, string actor, string attemptKind, CancellationToken cancellationToken)
    {
        try
        {
            if (await _runStore.HasSufficientTraceCapacityForDispatchAsync(candidate, current.LifecycleVersion, cancellationToken))
            {
                return null;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var cancelled = await CancelBeforeDispatchAsync(current, actor);
            return new RunAdvance(cancelled.Run, cancelled);
        }
        catch (Exception exception)
        {
            var terminal = await TerminateAsync(current, actor, CustomLoopRunStatus.Failed, "run_trace_capacity_check_failed", $"The durable run trace capacity check failed before the {attemptKind} request: {SafeExceptionClass(exception)}.");
            return new RunAdvance(terminal.Run, terminal);
        }

        var exhausted = await TerminateAsync(current, actor, CustomLoopRunStatus.Failed, "run_trace_capacity_exhausted", $"The durable run trace cannot reserve enough bounded space for another {attemptKind} attempt and its mandatory outcome evidence.");
        return new RunAdvance(exhausted.Run, exhausted);
    }

    private bool ExecutionDeadlineReached(CustomLoopRunRecord run) => GetAccumulatedRunningMilliseconds(run, Now(run)) >= CustomLoopLimits.MaxRunExecutionMilliseconds;

    private CancellationTokenSource CreateProviderToken(CustomLoopRunRecord run, CancellationToken callerToken)
    {
        var elapsed = GetAccumulatedRunningMilliseconds(run, Now(run));
        var remainingMilliseconds = Math.Max(1, CustomLoopLimits.MaxRunExecutionMilliseconds - elapsed);
        var source = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        source.CancelAfter(TimeSpan.FromMilliseconds(remainingMilliseconds));
        return source;
    }

    private async Task<CustomLoopOrderedRunResult> CancelAfterInterruptedPreDispatchPersistenceAsync(CustomLoopRunRecord current, CustomLoopRunRecord candidate, string actor)
    {
        CustomLoopRunRecord? latest;
        try
        {
            latest = await _runStore.GetAsync(current.Id, IntegrityToken());
        }
        catch (Exception exception)
        {
            return Result(CustomLoopOrderedRunStatus.NeedsReview, current, $"Caller cancellation interrupted a pre-dispatch trace write, and its durable outcome could not be loaded safely: {SafeExceptionClass(exception)}.");
        }

        if (latest is null)
        {
            return Result(CustomLoopOrderedRunStatus.NotFound, null, "Caller cancellation interrupted a pre-dispatch trace write, and the run trace could not be found.");
        }

        var matchesCurrent = DurableTraceVersionMatches(current, latest);
        var matchesCandidate = DurableTraceVersionMatches(candidate, latest);
        var matchesCandidateControlSuccessor = IsAcceptedControlSuccessor(candidate, latest);
        if (!CustomLoopRunValidator.Validate(latest).IsValid || !matchesCurrent && !matchesCandidate && !matchesCandidateControlSuccessor)
        {
            return Result(CustomLoopOrderedRunStatus.Conflict, latest, "Caller cancellation interrupted a pre-dispatch trace write, but the durable run changed outside the expected write or control transition; no provider request was dispatched.");
        }

        return await CancelBeforeDispatchAsync(latest, actor);
    }

    private async Task<RunAdvance> HandlePreInvocationCancellationAsync(CustomLoopRunRecord run, string actor, CancellationToken callerToken)
    {
        var boundary = await ObserveControlBoundaryAsync(run, actor);
        if (boundary.Terminal is not null)
        {
            return boundary;
        }

        run = boundary.Run!;
        if (callerToken.IsCancellationRequested)
        {
            var cancelled = await CancelBeforeDispatchAsync(run, actor);
            return new RunAdvance(cancelled.Run, cancelled);
        }

        if (ExecutionDeadlineReached(run))
        {
            var terminal = await TerminateAsync(run, actor, CustomLoopRunStatus.Failed, "run_deadline_exceeded", "The custom-loop execution deadline was reached before the provider request could start.");
            return new RunAdvance(terminal.Run, terminal);
        }

        var failed = await TerminateAsync(run, actor, CustomLoopRunStatus.Failed, "provider_cancelled_before_dispatch", "The provider request was cancelled before invocation without a matching caller, lifecycle, or deadline cancellation.");
        return new RunAdvance(failed.Run, failed);
    }

    private static bool DurableTraceVersionMatches(CustomLoopRunRecord expected, CustomLoopRunRecord actual)
    {
        return expected.LifecycleVersion == actual.LifecycleVersion
            && expected.Status == actual.Status
            && CheckpointsEqual(expected.Checkpoint, actual.Checkpoint)
            && expected.Events.Select(item => item.EventId).SequenceEqual(actual.Events.Select(item => item.EventId));
    }

    private static CustomLoopExecutionClock AdvanceClock(CustomLoopExecutionClock clock, DateTimeOffset now, bool terminal)
    {
        var accumulated = clock.AccumulatedRunningMilliseconds;
        if (clock.ActiveSinceUtc is { } activeSince)
        {
            accumulated = checked(accumulated + Math.Max(0, (long)(now - activeSince).TotalMilliseconds));
        }

        return new CustomLoopExecutionClock(Math.Min(accumulated, CustomLoopLimits.MaxRunExecutionMilliseconds), terminal ? null : now);
    }

    private static long GetAccumulatedRunningMilliseconds(CustomLoopRunRecord run, DateTimeOffset now)
    {
        return AdvanceClock(run.ExecutionClock, now, terminal: false).AccumulatedRunningMilliseconds;
    }

    private DateTimeOffset Now(CustomLoopRunRecord run)
    {
        var now = _timeProvider.GetUtcNow();
        return now < run.UpdatedAtUtc ? run.UpdatedAtUtc : now;
    }

    private static CanonicalOutput Canonicalize(string? output)
    {
        var exact = output ?? string.Empty;
        var originalCount = exact.Length;
        if (exact.Length <= CustomLoopLimits.MaxCanonicalModelOutputCharacters)
        {
            return new CanonicalOutput(exact, originalCount, false);
        }

        var length = CustomLoopLimits.MaxCanonicalModelOutputCharacters;
        if (length > 0 && char.IsHighSurrogate(exact[length - 1]) && length < exact.Length && char.IsLowSurrogate(exact[length]))
        {
            length--;
        }

        return new CanonicalOutput(exact[..length], originalCount, true);
    }

    private static CustomLoopExitDecision ParseExitDecision(string output)
    {
        var token = TrimAsciiWhitespace(output);
        if (string.Equals(token, "Complete", StringComparison.Ordinal))
        {
            return CustomLoopExitDecision.Complete;
        }

        return string.Equals(token, "Repeat", StringComparison.Ordinal) ? CustomLoopExitDecision.Repeat : CustomLoopExitDecision.Invalid;
    }

    private static string TrimAsciiWhitespace(string value)
    {
        var start = 0;
        while (start < value.Length && IsAsciiWhitespace(value[start]))
        {
            start++;
        }

        var end = value.Length - 1;
        while (end >= start && IsAsciiWhitespace(value[end]))
        {
            end--;
        }

        return value[start..(end + 1)];
    }

    private static bool IsAsciiWhitespace(char value) => value is ' ' or '\t' or '\r' or '\n' or '\f' or '\v';

    private static string PublicationOperationId(string runId, int iteration, string stepId, bool isExit)
    {
        var material = Encoding.UTF8.GetBytes($"{runId}\n{iteration}\n{(isExit ? "exit" : "inference")}\n{stepId}");
        return $"publish-{Convert.ToHexString(SHA256.HashData(material)).ToLowerInvariant()}";
    }

    private static CustomLoopPriorConversationPublication[] GetPriorConversationPublications(CustomLoopRunRecord run)
    {
        var publications = run.Events
            .Where(item => item is { Kind: CustomLoopRunEventKind.ConversationPublished, PublishedToInvokingConversation: true })
            .Select(item =>
            {
                var intent = run.Events.LastOrDefault(candidate => candidate.Sequence < item.Sequence && candidate.Kind == CustomLoopRunEventKind.ConversationPublicationStarted && candidate.Iteration == item.Iteration && string.Equals(candidate.StepId, item.StepId, StringComparison.Ordinal));
                if (item.Iteration is null || string.IsNullOrWhiteSpace(item.StepId) || item.CanonicalOutput is null || !CustomLoopArtifactIdentifier.IsValid(intent?.ConversationPublicationId))
                {
                    throw new FormatException("A successful conversation-publication event is missing its durable intent, coordinates, or exact canonical output.");
                }

                return new CustomLoopPriorConversationPublication(
                    intent!.ConversationPublicationId!,
                    item.CanonicalOutput,
                    CustomLoopTraceContentHash.Compute(item.CanonicalOutput));
            })
            .ToArray();
        if (publications.Length > CustomLoopLimits.MaxConversationPublicationEffectsPerRun)
        {
            throw new FormatException("The durable conversation-publication history exceeded the bounded model-attempt count.");
        }

        return publications;
    }

    private static string NewCorrelationId(string prefix)
    {
        return $"{prefix}-{Guid.NewGuid():N}";
    }

    private static CancellationToken IntegrityToken()
    {
        return new CancellationTokenSource(_integrityWriteTimeout).Token;
    }

    private static string SafeExceptionClass(Exception exception)
    {
        return exception.GetType().Name;
    }

    private static bool IsUncertainProviderFailure(Exception exception)
    {
        if (exception is OperationCanceledException or TimeoutException or IOException)
        {
            return true;
        }

        if (exception is AggregateException aggregate && aggregate.Flatten().InnerExceptions.Any(IsUncertainProviderFailure))
        {
            return true;
        }

        return exception.InnerException is not null && IsUncertainProviderFailure(exception.InnerException);
    }

    private static string? SafeReference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > CustomLoopLimits.MaxTraceReferenceCharacters || value.Any(character => char.IsControl(character) || char.IsSurrogate(character)))
        {
            return null;
        }

        return value.IsNormalized(NormalizationForm.FormC) ? value : null;
    }

    private static SequentialExecutionContext? CreateSequentialContext(
        int schemaVersion,
        GovernedLoopSequentialRunAnchor? anchor,
        GovernedLoopSequentialPlan? plan,
        GovernedLoopGraphRevisionArtifact? artifact,
        IGovernedLoopSequentialOrderedNodeEvidenceRecorder nodeEvidenceRecorder,
        IGovernedLoopSequentialAuditRecorder auditRecorder)
    {
        if (schemaVersion != GovernedLoopSequentialOrderedRunRequest.CurrentSchemaVersion
            || anchor is null
            || plan is null
            || artifact is null
            || !GovernedLoopSequentialContractValidator.Validate(anchor.AdapterBinding).IsValid
            || !GovernedLoopSequentialContractValidator.Validate(anchor.InvocationSnapshot).IsValid)
        {
            return null;
        }

        try
        {
            if (!string.Equals(GovernedLoopGraphRevisionContractHash.ComputeArtifactHash(artifact), artifact.ArtifactHash, StringComparison.Ordinal))
            {
                return null;
            }

            var allowedCapabilityIds = new List<CapabilityId>(artifact.Graph.AuthorityCeiling.CapabilityIds.Count);
            foreach (var value in artifact.Graph.AuthorityCeiling.CapabilityIds)
            {
                if (!CapabilityId.TryParse(value, out var capabilityId, out _))
                {
                    return null;
                }

                allowedCapabilityIds.Add(capabilityId!);
            }

            var rebuilt = GovernedLoopSequentialPlanBuilder.Build(artifact);
            if (rebuilt.Status != GovernedLoopSequentialPlanBuildStatus.Ready
                || rebuilt.Plan is null
                || !SequentialPlansEqual(plan, rebuilt.Plan))
            {
                return null;
            }

            var binding = anchor.AdapterBinding;
            if (!Equals(binding.ExecutionBinding.Revision, plan.Revision)
                || !Equals(artifact.RevisionArtifact.Revision, plan.Revision)
                || !string.Equals(binding.GraphArtifactHash, artifact.ArtifactHash, StringComparison.Ordinal)
                || !string.Equals(binding.GraphLayoutHash, artifact.LayoutHash, StringComparison.Ordinal)
                || !string.Equals(binding.InvocationPayloadHash, anchor.InvocationSnapshot.ContentHash, StringComparison.Ordinal))
            {
                return null;
            }

            return new SequentialExecutionContext(anchor, plan, artifact, allowedCapabilityIds.AsReadOnly(), nodeEvidenceRecorder, auditRecorder);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool SequentialPlansEqual(GovernedLoopSequentialPlan left, GovernedLoopSequentialPlan right)
        => left.SchemaVersion == right.SchemaVersion
            && Equals(left.Revision, right.Revision)
            && string.Equals(left.GraphArtifactHash, right.GraphArtifactHash, StringComparison.Ordinal)
            && string.Equals(left.GraphLayoutHash, right.GraphLayoutHash, StringComparison.Ordinal)
            && left.Nodes.Count == right.Nodes.Count
            && left.Nodes.Zip(right.Nodes).All(pair => pair.First.Ordinal == pair.Second.Ordinal
                && string.Equals(pair.First.NodeId, pair.Second.NodeId, StringComparison.Ordinal)
                && Equals(pair.First.Descriptor, pair.Second.Descriptor)
                && string.Equals(pair.First.IncomingControlEdgeId, pair.Second.IncomingControlEdgeId, StringComparison.Ordinal)
                && string.Equals(pair.First.OutgoingControlEdgeId, pair.Second.OutgoingControlEdgeId, StringComparison.Ordinal));

    private static bool SequentialRunMatches(CustomLoopRunRecord run, SequentialExecutionContext context)
    {
        var binding = context.Anchor.AdapterBinding;
        var invocation = context.Anchor.InvocationSnapshot;
        var graph = context.Artifact.Graph;
        var definition = run.AdmittedDefinition;
        var projection = GovernedLoopSequentialLegacyDefinitionProjector.Project(
            binding,
            invocation,
            context.Plan,
            context.Artifact);
        if (projection.Status != GovernedLoopSequentialLegacyDefinitionProjectionStatus.Ready
            || projection.Definition is not { } projectedDefinition)
        {
            return false;
        }

        if (!string.Equals(run.Id, binding.ExecutionBinding.RunId, StringComparison.Ordinal)
            || !string.Equals(run.LoopId, graph.GraphId, StringComparison.Ordinal)
            || !string.Equals(run.AdmissionOperationId, binding.AdmissionOperationId, StringComparison.Ordinal)
            || !string.Equals(run.TriggerPrompt, invocation.TriggerPrompt, StringComparison.Ordinal)
            || !Equals(run.ModelSnapshot, invocation.ModelSnapshot)
            || !Equals(run.InvokingConversation, invocation.InvokingConversation)
            || run.ContextSnapshot.CapturedAtUtc != invocation.ContextCapturedAtUtc
            || !run.ContextSnapshot.SourceManifest.SequenceEqual(invocation.ContextManifest)
            || !string.Equals(run.SequentialAdapterBinding?.ContentHash, binding.ContentHash, StringComparison.Ordinal)
            || !string.Equals(run.SequentialInvocationSnapshot?.ContentHash, invocation.ContentHash, StringComparison.Ordinal)
            || !string.Equals(definition.RoleId, graph.OwningRole.Identity.RoleId, StringComparison.Ordinal)
            || definition.InferenceSteps.Length != context.Plan.Nodes.Count - 2
            || !IsExactSequentialCapabilitySet(context.AllowedCapabilityIds)
            || !run.CapabilityAdmission.Pins.Select(pin => pin.DescriptorIdentity.Id).Order().SequenceEqual(context.AllowedCapabilityIds.Order())
            || !CustomLoopDefinitionContentHash.Matches(definition)
            || !string.Equals(definition.ContentHash, projectedDefinition.ContentHash, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static bool IsExactSequentialCapabilitySet(IReadOnlyList<CapabilityId> capabilityIds)
    {
        var values = capabilityIds.Select(item => item.Value).ToArray();
        return values.SequenceEqual(
                [SequentialConversationTurnCapabilityId, SequentialModelInferenceCapabilityId],
                StringComparer.Ordinal)
            || values.SequenceEqual(
                [SequentialConversationTurnCapabilityId, SequentialModelInferenceCapabilityId, SequentialWorkspaceCommandCapabilityId],
                StringComparer.Ordinal);
    }

    private static GovernedLoopSequentialNodeHandlerResultStatus SequentialDisposition(CustomLoopSequentialNodeEvidence? evidence)
        => evidence?.Disposition switch
        {
            CustomLoopSequentialNodeDisposition.Completed => GovernedLoopSequentialNodeHandlerResultStatus.Completed,
            CustomLoopSequentialNodeDisposition.Rejected => GovernedLoopSequentialNodeHandlerResultStatus.Rejected,
            CustomLoopSequentialNodeDisposition.NeedsReview => GovernedLoopSequentialNodeHandlerResultStatus.NeedsReview,
            _ => GovernedLoopSequentialNodeHandlerResultStatus.Unknown,
        };

    private static bool DispatchMatches(
        GovernedLoopSequentialNodeDispatchStatus dispatch,
        GovernedLoopSequentialNodeHandlerResultStatus disposition)
        => dispatch switch
        {
            GovernedLoopSequentialNodeDispatchStatus.Completed => disposition == GovernedLoopSequentialNodeHandlerResultStatus.Completed,
            GovernedLoopSequentialNodeDispatchStatus.Rejected => disposition == GovernedLoopSequentialNodeHandlerResultStatus.Rejected,
            GovernedLoopSequentialNodeDispatchStatus.NeedsReview => disposition == GovernedLoopSequentialNodeHandlerResultStatus.NeedsReview,
            _ => false,
        };

    private static int SequentialDispatchAttempt(
        CustomLoopRunRecord run,
        GovernedLoopSequentialPlanNode node)
    {
        var terminal = run.Events.LastOrDefault(item => item.SequentialNodeEvidence is
        {
            Kind: not CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
        } evidence
            && string.Equals(evidence.NodeId, node.NodeId, StringComparison.Ordinal));
        if (terminal?.SequentialNodeEvidence is { } terminalEvidence)
        {
            return IsResumablePauseRejection(run, terminal)
                ? checked(terminalEvidence.Attempt + 1)
                : terminalEvidence.Attempt;
        }

        return run.Events.LastOrDefault(item => item.SequentialNodeEvidence is
        {
            Kind: CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
        } evidence
            && string.Equals(evidence.NodeId, node.NodeId, StringComparison.Ordinal))?.SequentialNodeEvidence?.Attempt ?? 1;
    }

    private static bool IsResumablePauseRejection(CustomLoopRunRecord run, CustomLoopRunEvent terminal)
        => run.Status == CustomLoopRunStatus.Running
            && string.Equals(terminal.Detail, CanonicalPauseRejectionDetail, StringComparison.Ordinal)
            && run.Events.Any(item => item.Sequence > terminal.Sequence
                && item.Kind == CustomLoopRunEventKind.LifecycleChanged
                && item.Detail.Contains("entered Paused", StringComparison.Ordinal));

    private static CustomLoopRunEvent? FindSequentialNodeEvidence(
        CustomLoopRunRecord run,
        GovernedLoopSequentialPlanNode node,
        int attempt)
    {
        if (run.SequentialAdapterBinding is not { } binding)
        {
            return null;
        }

        var matches = run.Events.Where(item => item.SequentialNodeEvidence is { Kind: not CustomLoopSequentialNodeEvidenceKind.DispatchStarted } evidence
            && string.Equals(evidence.NodeId, node.NodeId, StringComparison.Ordinal)
            && evidence.Attempt == attempt
            && string.Equals(evidence.WorkspaceId, binding.WorkspaceId, StringComparison.Ordinal)
            && string.Equals(evidence.RunId, run.Id, StringComparison.Ordinal)
            && Equals(evidence.Revision, binding.ExecutionBinding.Revision)
            && evidence.ExecutionGeneration == binding.ExecutionBinding.ExecutionGeneration
            && CustomLoopSequentialNodeEvidenceHash.Matches(evidence)
            && CustomLoopSequentialOutcomeArtifactHash.Matches(item)
            && SequentialEvidenceEventMatchesNode(item, node.Descriptor.Kind))
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool SequentialEvidenceEventMatchesNode(
        CustomLoopRunEvent runEvent,
        EmbodySense.Core.Common.Loops.Models.Custom.Graph.GovernedLoopNodeKind nodeKind)
        => nodeKind switch
        {
            EmbodySense.Core.Common.Loops.Models.Custom.Graph.GovernedLoopNodeKind.Trigger
                => runEvent.Kind == CustomLoopRunEventKind.Admitted,
            EmbodySense.Core.Common.Loops.Models.Custom.Graph.GovernedLoopNodeKind.Inference
                => runEvent.Kind is CustomLoopRunEventKind.NodeAttemptCompleted or CustomLoopRunEventKind.NodeOutcomeObserved or CustomLoopRunEventKind.NodeAttemptFailed,
            EmbodySense.Core.Common.Loops.Models.Custom.Graph.GovernedLoopNodeKind.Exit
                => runEvent.Kind is CustomLoopRunEventKind.ExitDecisionCompleted or CustomLoopRunEventKind.NodeOutcomeObserved or CustomLoopRunEventKind.NodeAttemptFailed,
            _ => false,
        };

    private static CustomLoopOrderedRunResult Result(CustomLoopOrderedRunStatus status, CustomLoopRunRecord? run, string detail)
    {
        return new CustomLoopOrderedRunResult(status, run, detail);
    }

    private async Task<string?> GetCapabilityFailureAsync(
        CustomLoopRunRecord run,
        CancellationToken cancellationToken,
        IReadOnlyCollection<CapabilityId>? allowedCapabilityIds = null)
    {
        if (_capabilityAdmissionService is null)
        {
            return "No capability admission authority was composed for custom-loop execution.";
        }

        var allowed = allowedCapabilityIds ?? LoopCapabilityRequirements.GetAssignedCapabilityIds(run.AdmittedDefinition.CapabilityRequirements);
        var current = await _capabilityAdmissionService.RevalidateAsync(run.CapabilityAdmission, allowed, cancellationToken);
        return current.IsValid ? null : $"Custom-loop capability revalidation failed closed: {current.Detail}";
    }

    private sealed record RunAdvance(
        CustomLoopRunRecord? Run,
        CustomLoopOrderedRunResult? Terminal,
        CustomLoopRunCheckpoint? PendingCheckpoint = null,
        PendingTerminal? PendingTerminal = null);

    private sealed record PendingTerminal(
        CustomLoopRunStatus Status,
        string? FailureCode,
        string Detail,
        string? FinalOutput);

    private sealed record SequentialAuditBoundaryFailure(
        string FailureCode,
        string Detail);

    private sealed record SequentialExecutionContext(
        GovernedLoopSequentialRunAnchor Anchor,
        GovernedLoopSequentialPlan Plan,
        GovernedLoopGraphRevisionArtifact Artifact,
        IReadOnlyList<CapabilityId> AllowedCapabilityIds,
        IGovernedLoopSequentialOrderedNodeEvidenceRecorder NodeEvidenceRecorder,
        IGovernedLoopSequentialAuditRecorder AuditRecorder);

    private sealed record SequentialNodeExecutionContext(
        GovernedLoopSequentialAdapterBinding Binding,
        GovernedLoopGraphRevisionArtifact Artifact,
        GovernedLoopSequentialPlanNode Node,
        int Attempt,
        IReadOnlyList<CapabilityId> AllowedCapabilityIds,
        IGovernedLoopSequentialAuditRecorder AuditRecorder);

    private sealed record CanonicalOutput(string Text, int OriginalCharacterCount, bool Truncated);

}
