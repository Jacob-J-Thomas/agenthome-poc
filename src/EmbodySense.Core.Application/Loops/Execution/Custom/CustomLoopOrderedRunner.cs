using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.EffectAuthorityUsage;
using EmbodySense.Core.Application.Loops.EffectAuthorityUsage.Models;
using EmbodySense.Core.Application.Loops.EffectAuthorityEvidence.Models;
using EmbodySense.Core.Application.Loops.Execution.Authority;
using EmbodySense.Core.Application.Loops.Execution.Authority.Models;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.GraphValidation.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Loops.Execution.Authority;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.PureNodes;
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
    private static readonly UTF8Encoding _strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private const string SequentialConversationTurnCapabilityId = "org.embodysense/conversation-turn";
    private const string SequentialModelInferenceCapabilityId = "org.embodysense/model-inference";
    private const string SequentialWorkspaceCommandCapabilityId = "org.embodysense/workspace-command";
    private const string PublicationPublishedDetail = "Canonical output was published to the invoking conversation.";
    private const string PublicationAlreadyPublishedDetail = "Idempotent conversation publication was already committed.";
    private const string PublicationDefinitelyFailedDetail = "Conversation publication definitely failed; no success is reported.";
    private const string PublicationUncertainDetail = "Conversation publication outcome is uncertain and requires review.";
    private const string PublicationMismatchedIdentityDetail = "Conversation publisher returned an operation ID that did not match the durable publication intent.";
    private const string PublicationAuthorityUnprovenDetail = "Conversation publisher reported success without completing the exact canonical publication-authority boundary.";
    private const string PublicationUnsupportedDetail = "Conversation publisher returned an unsupported outcome that requires review.";
    private const string PublicationOmittedDetail = "Conversation publication was selected but omitted because admission bound no invoking conversation.";
    private const string CanonicalCallerCancellationDetail = "Caller cancellation rejected the canonical node before provider invocation.";
    private const string CanonicalDurableCancellationDetail = "Durable cancellation rejected the canonical node before provider invocation.";
    private const string CanonicalPureNodeCancellationDetail = "Caller cancellation rejected the deterministic pure node after its durable start marker.";
    private const string CanonicalPauseRejectionDetail = "A durable pause request rejected this canonical attempt before provider invocation; Resume may dispatch the next canonical attempt.";
    private const string CanonicalDeadlineRejectionDetail = "The custom-loop execution deadline was reached before the provider request could start.";
    private const string CanonicalPreProviderRejectionStartDetail = "Canonical node dispatch was retained before its pre-provider checks were rejected.";
    private const string CanonicalPureNodeFailureCodePrefix = "canonical-pure-node-failure-code-v1:";

    private readonly ICustomLoopRunStore _runStore;
    private readonly CustomLoopContextResolver _contextResolver;
    private readonly ICustomLoopInferenceAttemptExecutor _inferenceExecutor;
    private readonly ICustomLoopConversationPublisher _conversationPublisher;
    private readonly IAuditLog _auditLog;
    private readonly ICustomLoopToolAuthorityProvider _authorityProvider;
    private readonly ICustomLoopAttemptCancellationBroker? _attemptCancellationBroker;
    private readonly ICapabilityAdmissionService? _capabilityAdmissionService;
    private readonly IGovernedLoopConversationPublicationAuthorityBoundaryProvider? _conversationPublicationAuthorityBoundaryProvider;
    private readonly GovernedLoopFirstBoundRunCompletionBoundary? _firstBoundRunCompletionBoundary;
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
    /// <param name="conversationPublicationAuthorityBoundaryProvider">The canonical success-Exit publication authority-boundary provider. A missing provider leaves legacy execution unchanged but stops canonical publication.</param>
    /// <param name="firstBoundRunCompletionBoundary">The canonical success-Exit completion boundary. A missing boundary leaves legacy execution unchanged but stops canonical successful completion.</param>
    public CustomLoopOrderedRunner(
        ICustomLoopRunStore runStore,
        CustomLoopContextResolver contextResolver,
        ICustomLoopInferenceAttemptExecutor inferenceExecutor,
        ICustomLoopConversationPublisher conversationPublisher,
        IAuditLog auditLog,
        ICustomLoopToolAuthorityProvider authorityProvider,
        TimeProvider? timeProvider = null,
        ICustomLoopAttemptCancellationBroker? attemptCancellationBroker = null,
        ICapabilityAdmissionService? capabilityAdmissionService = null,
        IGovernedLoopConversationPublicationAuthorityBoundaryProvider? conversationPublicationAuthorityBoundaryProvider = null,
        GovernedLoopFirstBoundRunCompletionBoundary? firstBoundRunCompletionBoundary = null)
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
        _conversationPublicationAuthorityBoundaryProvider = conversationPublicationAuthorityBoundaryProvider;
        _firstBoundRunCompletionBoundary = firstBoundRunCompletionBoundary;
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

        if (sequentialContext is not null && run.Status == CustomLoopRunStatus.Completed)
        {
            return await CompleteCanonicalAsync(
                run,
                sequentialContext,
                "The exact durable canonical completion was replayed for grant-completion reconciliation.",
                run.FinalOutput ?? string.Empty);
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

            var started = await StartRunAsync(run, request.Actor, cancellationToken);
            if (started.Terminal is not null)
            {
                return started.Terminal;
            }

            return await ContinueRegisteredAsync(
                started.Run!,
                request.Actor,
                cancellationToken,
                sequentialContext,
                sequentialCapabilityFailure);
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

        if (sequentialContext is not null && run.Status == CustomLoopRunStatus.Completed)
        {
            return await CompleteCanonicalAsync(
                run,
                sequentialContext,
                "The exact durable canonical completion was replayed for grant-completion reconciliation.",
                run.FinalOutput ?? string.Empty);
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
        SequentialExecutionContext? sequentialContext = null,
        string? sequentialCapabilityFailure = null)
    {
        var dispatchState = new ProviderDispatchState();
        var result = sequentialContext is null
            ? await ContinueLegacyAsync(run, actor, dispatchState, cancellationToken)
            : await ContinueSequentialAsync(run, actor, dispatchState, cancellationToken, sequentialContext, sequentialCapabilityFailure);
        return result with { ProviderWasInvoked = dispatchState.ProviderWasInvoked };
    }

    private async Task<CustomLoopOrderedRunResult> ContinueLegacyAsync(
        CustomLoopRunRecord run,
        string actor,
        ProviderDispatchState dispatchState,
        CancellationToken cancellationToken)
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
                var advanced = await ExecuteInferenceStepAsync(run, step, actor, dispatchState, cancellationToken);
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
                return await CompleteDeterministicallyAsync(run, actor, Detail, cancellationToken);
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

    private async Task<CustomLoopOrderedRunResult> ContinueSequentialAsync(
        CustomLoopRunRecord run,
        string actor,
        ProviderDispatchState dispatchState,
        CancellationToken cancellationToken,
        SequentialExecutionContext context,
        string? sequentialCapabilityFailure)
    {
        while (true)
        {
            var hasOpenPureAttempt = HasOpenSequentialPureAttempt(run, context);
            var boundary = hasOpenPureAttempt
                ? await RefreshPureControlUpdateAsync(run)
                : await ObserveControlBoundaryAsync(run, actor);
            if (boundary.Terminal is not null)
            {
                return boundary.Terminal;
            }

            run = boundary.Run!;
            var selected = GovernedLoopSequentialFrontierMachine.Select(run.Frontier, context.Anchor.AdapterBinding, context.Plan);
            if (selected.Status == GovernedLoopSequentialFrontierSelectionStatus.Invalid)
            {
                return Result(CustomLoopOrderedRunStatus.InvalidState, run, selected.Detail);
            }

            if (selected.Status == GovernedLoopSequentialFrontierSelectionStatus.ReviewBlocked)
            {
                return Result(CustomLoopOrderedRunStatus.NeedsReview, run, selected.Detail);
            }

            if (selected.Status == GovernedLoopSequentialFrontierSelectionStatus.Terminal)
            {
                return await CompleteOrProjectSequentialTerminalAsync(run, actor, context);
            }

            if (cancellationToken.IsCancellationRequested && hasOpenPureAttempt)
            {
                var cancelledNode = selected.Node!;
                var cancelled = await DispatchSequentialNodeAsync(
                    context,
                    run,
                    cancelledNode,
                    selected.Attempt!.Value,
                    actor,
                    token => RejectSequentialPureNodeAsync(
                        run,
                        actor,
                        new SequentialNodeExecutionContext(
                            context.Anchor.AdapterBinding,
                            context.Artifact,
                            cancelledNode,
                            selected.Activation!,
                            selected.Attempt.Value,
                            selected.AttemptOperationId!,
                            context.AllowedCapabilityIds,
                            context.AuditRecorder),
                        null,
                        CanonicalPureNodeCancellationDetail,
                        CustomLoopRunStatus.Cancelled),
                    IntegrityToken());
                return cancelled.Terminal
                    ?? Result(CustomLoopOrderedRunStatus.NeedsReview, cancelled.Run, "Canonical pure-node cancellation did not produce one closed terminal disposition.");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return await CancelBeforeDispatchAsync(run, actor);
            }

            var node = selected.Node!;
            if (selected.Status == GovernedLoopSequentialFrontierSelectionStatus.Ready
                && sequentialCapabilityFailure is not null
                && Equals(node.Descriptor, GovernedLoopSequentialNodeDescriptors.ProviderInference))
            {
                var capabilityOperationId = NewCorrelationId("frontier-attempt");
                var capabilityClaim = await ClaimSequentialNodeAsync(run, context, node, capabilityOperationId, actor, cancellationToken);
                if (capabilityClaim.Terminal is not null)
                {
                    return capabilityClaim.Terminal;
                }

                var rejected = await DispatchSequentialNodeAsync(
                    context,
                    capabilityClaim.Run!,
                    node,
                    1,
                    actor,
                    token => RejectSequentialNodeBeforeProviderAsync(
                        capabilityClaim.Run!,
                        actor,
                        new SequentialNodeExecutionContext(
                            context.Anchor.AdapterBinding,
                            context.Artifact,
                            node,
                            RequireRunningSequentialActivation(capabilityClaim.Run!, node, 1, capabilityOperationId),
                            1,
                            capabilityOperationId,
                            context.AllowedCapabilityIds,
                            context.AuditRecorder),
                        node.NodeId,
                        isExit: false,
                        "canonical_run_capability_invalid",
                        sequentialCapabilityFailure),
                    cancellationToken);
                return rejected.Terminal
                    ?? Result(CustomLoopOrderedRunStatus.NeedsReview, rejected.Run, "Canonical capability rejection did not produce a closed terminal disposition.");
            }

            var deadlineReached = GetAccumulatedRunningMilliseconds(run, Now(run)) >= CustomLoopLimits.MaxRunExecutionMilliseconds;
            if (selected.Status == GovernedLoopSequentialFrontierSelectionStatus.Running)
            {
                var retained = FindSequentialNodeEvidence(run, node, selected.Activation!, selected.Attempt!.Value);
                var isPure = GovernedLoopSequentialNodeDescriptors.IsPure(node.Descriptor);
                if (retained is null && !isPure)
                {
                    return await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, "canonical_open_frontier_attempt_requires_review", "The durable frontier contains an open Running attempt without one exact terminal evidence record; automatic redispatch is forbidden.");
                }

                if (retained is null && isPure && HasSequentialTerminalCandidate(run, node, selected.Activation!, selected.Attempt.Value))
                {
                    return await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, "canonical_pure_outcome_reconciliation_failed", "A terminal pure-node event exists but does not authenticate as the exact retained outcome; automatic evaluation is forbidden.");
                }

                var started = FindSequentialDispatchStart(run, node, selected.Activation!, selected.Attempt.Value, selected.AttemptOperationId!);
                if (started is null || retained is not null && retained.Sequence <= started.Sequence)
                {
                    return await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, "canonical_frontier_reconciliation_failed", "The retained terminal node evidence is not causally bound to the exact frontier attempt operation; automatic redispatch is forbidden.");
                }

                if (retained is null && isPure && deadlineReached)
                {
                    var sequentialNode = new SequentialNodeExecutionContext(
                        context.Anchor.AdapterBinding,
                        context.Artifact,
                        node,
                        selected.Activation!,
                        selected.Attempt.Value,
                        selected.AttemptOperationId!,
                        context.AllowedCapabilityIds,
                        context.AuditRecorder);
                    var rejected = await DispatchSequentialNodeAsync(
                        context,
                        run,
                        node,
                        selected.Attempt.Value,
                        actor,
                        token => RejectSequentialPureNodeAsync(
                            run,
                            actor,
                            sequentialNode,
                            "run_deadline_exceeded",
                            CanonicalDeadlineRejectionDetail),
                        cancellationToken);
                    return rejected.Terminal
                        ?? Result(CustomLoopOrderedRunStatus.NeedsReview, rejected.Run, "Canonical pure-node deadline rejection did not produce one closed terminal disposition.");
                }

                var reconciled = await DispatchSelectedSequentialNodeAsync(
                    context,
                    run,
                    node,
                    selected.Attempt.Value,
                    selected.AttemptOperationId!,
                    actor,
                    dispatchState,
                    cancellationToken);
                if (reconciled.Terminal is not null)
                {
                    return reconciled.Terminal;
                }

                run = reconciled.Run!;
                continue;
            }

            if (deadlineReached)
            {
                var deadlineOperationId = NewCorrelationId("frontier-attempt");
                var isPure = GovernedLoopSequentialNodeDescriptors.IsPure(node.Descriptor);
                var deadlineClaim = isPure
                    ? await ClaimSequentialPureNodeAsync(run, context, node, deadlineOperationId, actor, cancellationToken)
                    : await ClaimSequentialNodeAsync(run, context, node, deadlineOperationId, actor, cancellationToken);
                if (deadlineClaim.Terminal is not null)
                {
                    return deadlineClaim.Terminal;
                }

                var isExit = Equals(node.Descriptor, GovernedLoopSequentialNodeDescriptors.SuccessExit);
                var rejected = await DispatchSequentialNodeAsync(
                    context,
                    deadlineClaim.Run!,
                    node,
                    1,
                    actor,
                    token => isPure
                        ? RejectSequentialPureNodeAsync(
                            deadlineClaim.Run!,
                            actor,
                            new SequentialNodeExecutionContext(context.Anchor.AdapterBinding, context.Artifact, node, RequireRunningSequentialActivation(deadlineClaim.Run!, node, 1, deadlineOperationId), 1, deadlineOperationId, context.AllowedCapabilityIds, context.AuditRecorder),
                            "run_deadline_exceeded",
                            CanonicalDeadlineRejectionDetail)
                        : RejectSequentialNodeBeforeProviderAsync(
                            deadlineClaim.Run!,
                            actor,
                            new SequentialNodeExecutionContext(context.Anchor.AdapterBinding, context.Artifact, node, RequireRunningSequentialActivation(deadlineClaim.Run!, node, 1, deadlineOperationId), 1, deadlineOperationId, context.AllowedCapabilityIds, context.AuditRecorder),
                            isExit ? "exit" : node.NodeId,
                            isExit,
                            "run_deadline_exceeded",
                            CanonicalDeadlineRejectionDetail),
                    cancellationToken);
                return rejected.Terminal
                    ?? Result(CustomLoopOrderedRunStatus.NeedsReview, rejected.Run, "Canonical deadline rejection did not produce one closed terminal disposition.");
            }

            var operationId = NewCorrelationId("frontier-attempt");
            var claimed = GovernedLoopSequentialNodeDescriptors.IsPure(node.Descriptor)
                ? await ClaimSequentialPureNodeAsync(run, context, node, operationId, actor, cancellationToken)
                : GovernedLoopSequentialNodeDescriptors.IsTopology(node.Descriptor)
                    ? await ClaimSequentialTopologyNodeAsync(run, context, node, operationId, actor, cancellationToken)
                : await ClaimSequentialNodeAsync(run, context, node, operationId, actor, cancellationToken);
            if (claimed.Terminal is not null)
            {
                return claimed.Terminal;
            }

            run = claimed.Run!;
            var advanced = await DispatchSelectedSequentialNodeAsync(
                context,
                run,
                node,
                1,
                operationId,
                actor,
                dispatchState,
                cancellationToken);
            if (advanced.Terminal is not null)
            {
                return advanced.Terminal;
            }

            run = advanced.Run!;
        }
    }

    private async Task<RunAdvance> ClaimSequentialPureNodeAsync(
        CustomLoopRunRecord run,
        SequentialExecutionContext context,
        GovernedLoopSequentialPlanNode node,
        string attemptOperationId,
        string actor,
        CancellationToken cancellationToken)
    {
        if (!GovernedLoopSequentialNodeDescriptors.IsPure(node.Descriptor))
        {
            return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.InvalidState, run, "Only Transform and Validate nodes may use the atomic pure-node claim."));
        }

        var now = Now(run);
        var selection = GovernedLoopSequentialFrontierMachine.Select(run.Frontier, context.Anchor.AdapterBinding, context.Plan);
        var transition = GovernedLoopSequentialFrontierMachine.Start(
            run.Frontier,
            context.Anchor.AdapterBinding,
            context.Plan,
            node,
            selection.Activation,
            1,
            attemptOperationId,
            now);
        if (transition.Status != GovernedLoopSequentialFrontierTransitionStatus.Applied || transition.Frontier is null)
        {
            return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.InvalidState, run, transition.Detail));
        }

        var sequentialNode = new SequentialNodeExecutionContext(
            context.Anchor.AdapterBinding,
            context.Artifact,
            node,
            transition.Frontier.Payload.Nodes[selection.Activation!.ActivationOrdinal],
            1,
            attemptOperationId,
            context.AllowedCapabilityIds,
            context.AuditRecorder);
        var start = Event(
            run,
            now,
            CustomLoopRunEventKind.NodeAttemptStarted,
            "Deterministic pure-node dispatch was retained before evaluation.",
            run.Checkpoint.Iteration,
            node.NodeId,
            1,
            traceReservationUtf8Bytes: CustomLoopLimits.MaxGraphPureNodeOutcomeEvidenceReservationUtf8Bytes,
            eventId: attemptOperationId);
        start = WithSequentialEvidence(start, sequentialNode, CustomLoopSequentialNodeEvidenceKind.DispatchStarted, CustomLoopSequentialNodeDisposition.Unknown);
        var candidate = Append(run, now, [start]) with { Frontier = transition.Frontier };
        var capacityBoundary = await RejectUnavailableTraceCapacityAsync(run, candidate, actor, "pure-node", cancellationToken);
        if (capacityBoundary is not null)
        {
            return capacityBoundary;
        }

        try
        {
            return await PersistAsync(run, candidate, cancellationToken, outcomeMayExist: false, propagateCancellation: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CustomLoopRunRecord? latest;
            try
            {
                latest = await _runStore.GetAsync(run.Id, IntegrityToken());
            }
            catch (Exception exception)
            {
                return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.NeedsReview, run, $"Caller cancellation interrupted the atomic pure-node claim, and its durable outcome could not be loaded safely: {SafeExceptionClass(exception)}."));
            }

            if (latest is not null && FindSequentialDispatchStart(latest, node, sequentialNode.Activation, 1, attemptOperationId) is not null)
            {
                return await RejectSequentialPureNodeAsync(latest, actor, sequentialNode, null, CanonicalPureNodeCancellationDetail, CustomLoopRunStatus.Cancelled);
            }

            var cancelled = await CancelBeforeDispatchAsync(latest ?? run, actor);
            return new RunAdvance(cancelled.Run, cancelled);
        }
    }

    private async Task<RunAdvance> ClaimSequentialNodeAsync(
        CustomLoopRunRecord run,
        SequentialExecutionContext context,
        GovernedLoopSequentialPlanNode node,
        string attemptOperationId,
        string actor,
        CancellationToken cancellationToken)
    {
        var now = Now(run);
        var selection = GovernedLoopSequentialFrontierMachine.Select(run.Frontier, context.Anchor.AdapterBinding, context.Plan);
        var transition = GovernedLoopSequentialFrontierMachine.Start(
            run.Frontier,
            context.Anchor.AdapterBinding,
            context.Plan,
            node,
            selection.Activation,
            1,
            attemptOperationId,
            now);
        if (transition.Status != GovernedLoopSequentialFrontierTransitionStatus.Applied || transition.Frontier is null)
        {
            return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.InvalidState, run, transition.Detail));
        }

        var candidate = run with
        {
            LifecycleVersion = checked(run.LifecycleVersion + 1),
            UpdatedAtUtc = now,
            Frontier = transition.Frontier,
        };
        try
        {
            return await PersistAsync(run, candidate, cancellationToken, outcomeMayExist: false, propagateCancellation: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var latest = await _runStore.GetAsync(run.Id, IntegrityToken());
            if (latest?.Frontier is { } latestFrontier
                && latestFrontier.Payload.Nodes.SingleOrDefault(candidate =>
                    candidate.Status == GovernedLoopNodeExecutionStatus.Running
                    && string.Equals(candidate.AttemptOperationId, attemptOperationId, StringComparison.Ordinal)) is not null)
            {
                var cancelled = await CancelBeforeDispatchAsync(latest, actor);
                return new RunAdvance(cancelled.Run, cancelled);
            }

            var terminal = await CancelBeforeDispatchAsync(run, actor);
            return new RunAdvance(terminal.Run, terminal);
        }
    }

    private async Task<RunAdvance> ClaimSequentialTopologyNodeAsync(
        CustomLoopRunRecord run,
        SequentialExecutionContext context,
        GovernedLoopSequentialPlanNode node,
        string attemptOperationId,
        string actor,
        CancellationToken cancellationToken)
    {
        if (!GovernedLoopSequentialNodeDescriptors.IsTopology(node.Descriptor))
        {
            return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.InvalidState, run, "Only an admitted Condition or Join may use the atomic topology-node claim."));
        }

        var selection = GovernedLoopSequentialFrontierMachine.Select(run.Frontier, context.Anchor.AdapterBinding, context.Plan);
        var now = Now(run);
        var started = GovernedLoopSequentialFrontierMachine.Start(
            run.Frontier,
            context.Anchor.AdapterBinding,
            context.Plan,
            node,
            selection.Activation,
            1,
            attemptOperationId,
            now);
        if (started.Status != GovernedLoopSequentialFrontierTransitionStatus.Applied || started.Frontier is null)
        {
            return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.InvalidState, run, started.Detail));
        }

        var activation = started.Frontier.Payload.Nodes[selection.Activation!.ActivationOrdinal];
        var execution = new SequentialNodeExecutionContext(
            context.Anchor.AdapterBinding,
            context.Artifact,
            node,
            activation,
            1,
            attemptOperationId,
            context.AllowedCapabilityIds,
            context.AuditRecorder);
        var startEvent = Event(
            run,
            now,
            CustomLoopRunEventKind.NodeAttemptStarted,
            "Deterministic topology-node dispatch and its terminal route are committed atomically.",
            activation.CycleIteration ?? 1,
            node.NodeId,
            1,
            traceReservationUtf8Bytes: CustomLoopLimits.MaxGraphPureNodeOutcomeEvidenceReservationUtf8Bytes,
            eventId: attemptOperationId);
        startEvent = WithSequentialEvidence(startEvent, execution, CustomLoopSequentialNodeEvidenceKind.DispatchStarted, CustomLoopSequentialNodeDisposition.Unknown);

        GovernedLoopConditionEvaluationResult? evaluation = null;
        if (node.Descriptor.Kind == GovernedLoopNodeKind.Condition)
        {
            var binding = GovernedLoopSequentialBindingResolver.Resolve(context.Artifact, context.Plan, node, selection.Activation, run);
            var graphNode = context.Artifact.Graph.Nodes.SingleOrDefault(candidate => string.Equals(candidate.Id, node.NodeId, StringComparison.Ordinal));
            if (binding.IsResolved && binding.Inputs.Count == 1 && graphNode is not null)
            {
                evaluation = GovernedLoopConditionEvaluator.Evaluate(graphNode, binding.Inputs[0].Value);
            }
        }

        var isCompleted = node.Descriptor.Kind == GovernedLoopNodeKind.Join
            || evaluation?.Status == GovernedLoopConditionEvaluationStatus.Selected;
        var outcome = node.Descriptor.Kind == GovernedLoopNodeKind.Join
            ? GovernedLoopControlCondition.Success
            : evaluation?.SelectedOutcome ?? GovernedLoopControlCondition.Unknown;
        var startOwner = run with { Events = [.. run.Events, startEvent] };
        var terminalEvent = Event(
            startOwner,
            now,
            isCompleted ? CustomLoopRunEventKind.NodeAttemptCompleted : CustomLoopRunEventKind.NodeAttemptFailed,
            isCompleted
                ? "The deterministic topology-node route was retained before any successor could dispatch."
                : $"The deterministic Condition could not select an admitted route ({evaluation?.ErrorCode ?? "condition.binding.unresolved"}).",
            activation.CycleIteration ?? 1,
            node.NodeId,
            1);
        terminalEvent = isCompleted
            ? WithSequentialEvidence(terminalEvent, execution, CustomLoopSequentialNodeEvidenceKind.CompletedOutcome, CustomLoopSequentialNodeDisposition.Completed, outcome)
            : WithSequentialEvidence(terminalEvent, execution, CustomLoopSequentialNodeEvidenceKind.AmbiguityAttention, CustomLoopSequentialNodeDisposition.NeedsReview);

        var frontier = started.Frontier;
        if (!isCompleted)
        {
            var blocked = GovernedLoopSequentialFrontierMachine.ReviewBlockRunning(
                started.Frontier,
                context.Anchor.AdapterBinding,
                context.Plan,
                node,
                activation,
                1,
                attemptOperationId,
                terminalEvent.EventId,
                terminalEvent.SequentialNodeEvidence!.OutcomeArtifactHash,
                now);
            if (blocked.Status != GovernedLoopSequentialFrontierTransitionStatus.Applied || blocked.Frontier is null)
            {
                return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.InvalidState, run, blocked.Detail));
            }

            frontier = blocked.Frontier;
        }

        var candidate = Append(run, now, [startEvent, terminalEvent]) with { Frontier = frontier };
        var capacityBoundary = await RejectUnavailableTraceCapacityAsync(run, candidate, actor, "topology-node", cancellationToken);
        if (capacityBoundary is not null)
        {
            return capacityBoundary;
        }

        var persisted = await PersistAsync(run, candidate, cancellationToken, outcomeMayExist: false, propagateCancellation: true);
        if (persisted.Terminal is not null || isCompleted)
        {
            return persisted;
        }

        var detail = terminalEvent.Detail;
        return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.NeedsReview, persisted.Run, detail));
    }

    private async Task<RunAdvance> DispatchSelectedSequentialNodeAsync(
        SequentialExecutionContext context,
        CustomLoopRunRecord run,
        GovernedLoopSequentialPlanNode node,
        int attempt,
        string attemptOperationId,
        string actor,
        ProviderDispatchState dispatchState,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested
            && !GovernedLoopSequentialNodeDescriptors.IsPure(node.Descriptor))
        {
            var cancelled = await CancelBeforeDispatchAsync(run, actor);
            return new RunAdvance(cancelled.Run, cancelled);
        }

        if (node.Descriptor.Kind == EmbodySense.Core.Common.Loops.Models.Custom.Graph.GovernedLoopNodeKind.Inference)
        {
            var stepIndex = context.Plan.Nodes.Take(node.Ordinal).Count(item => Equals(item.Descriptor, GovernedLoopSequentialNodeDescriptors.ProviderInference));
            if (stepIndex < 0 || stepIndex >= run.AdmittedDefinition.InferenceSteps.Length)
            {
                return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.InvalidState, run, "The selected frontier inference ordinal has no exact admitted legacy projection."));
            }

            return await DispatchAndAdvanceSequentialInferenceAsync(
                context,
                run,
                node,
                attempt,
                attemptOperationId,
                run.AdmittedDefinition.InferenceSteps[stepIndex],
                actor,
                dispatchState,
                cancellationToken);
        }

        if (Equals(node.Descriptor, GovernedLoopSequentialNodeDescriptors.SuccessExit))
        {
            const string Detail = "Continuation is disabled; Exit completed without a model call.";
            var terminal = await DispatchAndAdvanceSequentialExitAsync(
                context,
                run,
                node,
                attempt,
                attemptOperationId,
                actor,
                Detail,
                cancellationToken);
            return new RunAdvance(terminal.Run, terminal);
        }

        if (GovernedLoopSequentialNodeDescriptors.IsPure(node.Descriptor))
        {
            return await DispatchAndAdvanceSequentialPureNodeAsync(
                context,
                run,
                node,
                attempt,
                attemptOperationId,
                actor,
                cancellationToken);
        }

        if (GovernedLoopSequentialNodeDescriptors.IsTopology(node.Descriptor))
        {
            return await DispatchAndAdvanceSequentialTopologyNodeAsync(
                context,
                run,
                node,
                attempt,
                attemptOperationId,
                actor,
                cancellationToken);
        }

        return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.InvalidState, run, "The schema-1 sequential frontier selected an unsupported canonical node family."));
    }

    private async Task<RunAdvance> DispatchAndAdvanceSequentialTopologyNodeAsync(
        SequentialExecutionContext context,
        CustomLoopRunRecord run,
        GovernedLoopSequentialPlanNode node,
        int attempt,
        string attemptOperationId,
        string actor,
        CancellationToken cancellationToken)
        => await DispatchSequentialNodeAsync(
            context,
            run,
            node,
            attempt,
            actor,
            _ => ReconcileAndAdvanceSequentialTopologyNodeAsync(context, run, node, attempt, attemptOperationId, actor),
            cancellationToken);

    private async Task<RunAdvance> ReconcileAndAdvanceSequentialTopologyNodeAsync(
        SequentialExecutionContext context,
        CustomLoopRunRecord run,
        GovernedLoopSequentialPlanNode node,
        int attempt,
        string attemptOperationId,
        string actor)
    {
        var activation = RequireRunningSequentialActivation(run, node, attempt, attemptOperationId);
        var terminalEvidence = FindSequentialNodeEvidence(run, node, activation, attempt);
        if (terminalEvidence?.SequentialNodeEvidence is not
            {
                Kind: CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
                Disposition: CustomLoopSequentialNodeDisposition.Completed,
                ControlOutcome: { } controlOutcome,
            }
            || node.Descriptor.Kind == GovernedLoopNodeKind.Condition
                && controlOutcome is not (GovernedLoopControlCondition.True or GovernedLoopControlCondition.False)
            || node.Descriptor.Kind == GovernedLoopNodeKind.Join
                && controlOutcome != GovernedLoopControlCondition.Success)
        {
            var invalid = await TerminateAsync(
                run,
                actor,
                CustomLoopRunStatus.NeedsReview,
                "canonical_topology_outcome_missing",
                "The Running topology activation has no exact retained terminal route; deterministic re-evaluation is forbidden.");
            return new RunAdvance(invalid.Run, invalid);
        }

        var completion = CompleteSequentialFrontier(run, context, node, attempt, attemptOperationId);
        if (completion is null)
        {
            var invalid = await TerminateAsync(
                run,
                actor,
                CustomLoopRunStatus.NeedsReview,
                "canonical_topology_frontier_advancement_failed",
                "The retained topology route could not atomically advance its exact frontier and pruning evidence.");
            return new RunAdvance(invalid.Run, invalid);
        }

        var now = Now(run);
        var candidate = Append(run, now, completion.SkipEvents) with
        {
            ExecutionClock = AdvanceClock(run.ExecutionClock, now, terminal: false),
            Frontier = completion.Frontier,
        };
        var persisted = await PersistAsync(run, candidate, IntegrityToken(), outcomeMayExist: false);
        if (persisted.Terminal?.Status != CustomLoopOrderedRunStatus.Conflict)
        {
            return persisted;
        }

        CustomLoopRunRecord? latest;
        try
        {
            latest = await _runStore.GetAsync(run.Id, IntegrityToken());
        }
        catch (Exception exception)
        {
            return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.NeedsReview, run, $"Topology-frontier reconciliation could not load the competing successor: {SafeExceptionClass(exception)}."));
        }

        if (latest is not null
            && CustomLoopRunValidator.HasExactDurableEventPrefix(run, latest)
            && latest.Frontier?.Payload.Nodes.ElementAtOrDefault(activation.ActivationOrdinal) is
            {
                Status: GovernedLoopNodeExecutionStatus.Completed,
                OutcomeEvidenceId: { } outcomeEvidenceId,
                OutcomeEvidenceHash: { } outcomeEvidenceHash,
            } completed
            && completed.VisitOrdinal == activation.VisitOrdinal
            && string.Equals(outcomeEvidenceId, terminalEvidence.EventId, StringComparison.Ordinal)
            && string.Equals(outcomeEvidenceHash, terminalEvidence.SequentialNodeEvidence.OutcomeArtifactHash, StringComparison.Ordinal))
        {
            return new RunAdvance(latest, null);
        }

        return persisted;
    }

    private async Task<CustomLoopOrderedRunResult> CompleteOrProjectSequentialTerminalAsync(
        CustomLoopRunRecord run,
        string actor,
        SequentialExecutionContext context)
    {
        return run.Frontier!.Payload.Status switch
        {
            GovernedLoopFrontierStatus.Completed when run.Status == CustomLoopRunStatus.Completed
                => await CompleteCanonicalAsync(run, context, "The exact durable canonical completion was replayed for grant-completion reconciliation.", run.FinalOutput ?? string.Empty),
            GovernedLoopFrontierStatus.Failed when run.Status == CustomLoopRunStatus.Failed
                => Result(CustomLoopOrderedRunStatus.Failed, run, run.FailureDetail ?? "The canonical frontier failed."),
            GovernedLoopFrontierStatus.ReviewBlocked when run.Status == CustomLoopRunStatus.NeedsReview
                => Result(CustomLoopOrderedRunStatus.NeedsReview, run, run.FailureDetail ?? "The canonical frontier requires review."),
            GovernedLoopFrontierStatus.Cancelled when run.Status == CustomLoopRunStatus.Cancelled
                => Result(CustomLoopOrderedRunStatus.Cancelled, run, "The canonical frontier was cancelled."),
            _ => Result(CustomLoopOrderedRunStatus.InvalidState, run, $"The terminal canonical frontier does not compose with lifecycle `{run.Status}`; no work was dispatched."),
        };
    }

    private static CustomLoopRunEvent? FindSequentialDispatchStart(
        CustomLoopRunRecord run,
        GovernedLoopSequentialPlanNode node,
        GovernedLoopNodeExecutionEvidence activation,
        int attempt,
        string attemptOperationId)
    {
        var matches = run.Events.Where(item => string.Equals(item.EventId, attemptOperationId, StringComparison.Ordinal)
            && item.Attempt == attempt
            && item.SequentialNodeEvidence is
            {
                Kind: CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
                Disposition: CustomLoopSequentialNodeDisposition.Unknown,
            } evidence
            && evidence.ActivationOrdinal == activation.ActivationOrdinal
            && evidence.VisitOrdinal == activation.VisitOrdinal
            && string.Equals(evidence.NodeId, node.NodeId, StringComparison.Ordinal)
            && evidence.Attempt == attempt
            && string.Equals(evidence.CycleId, activation.CycleId, StringComparison.Ordinal)
            && evidence.CycleIteration == activation.CycleIteration
            && CustomLoopSequentialNodeEvidenceHash.Matches(evidence)
            && CustomLoopSequentialOutcomeArtifactHash.Matches(item)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static GovernedLoopNodeExecutionEvidence RequireRunningSequentialActivation(
        CustomLoopRunRecord run,
        GovernedLoopSequentialPlanNode node,
        int attempt,
        string attemptOperationId)
        => run.Frontier?.Payload.Nodes.SingleOrDefault(candidate =>
            candidate.Status == GovernedLoopNodeExecutionStatus.Running
            && candidate.PlanOrdinal == node.Ordinal
            && string.Equals(candidate.NodeId, node.NodeId, StringComparison.Ordinal)
            && candidate.Attempt == attempt
            && string.Equals(candidate.AttemptOperationId, attemptOperationId, StringComparison.Ordinal))
        ?? throw new InvalidOperationException("The exact Running activation is unavailable for canonical node dispatch or evidence attribution.");

    private SequentialFrontierCompletion? CompleteSequentialFrontier(
        CustomLoopRunRecord run,
        SequentialExecutionContext context,
        GovernedLoopSequentialPlanNode node,
        int attempt,
        string attemptOperationId)
    {
        var activation = RequireRunningSequentialActivation(run, node, attempt, attemptOperationId);
        var evidence = FindSequentialNodeEvidence(run, node, activation, attempt);
        if (evidence?.SequentialNodeEvidence is not
            {
                Kind: CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
                Disposition: CustomLoopSequentialNodeDisposition.Completed,
            } sequentialEvidence)
        {
            return null;
        }

        var controlOutcome = sequentialEvidence.ControlOutcome ?? GovernedLoopControlCondition.Success;
        var pruning = GovernedLoopSequentialFrontierMachine.PlanPruning(
            run.Frontier,
            context.Anchor.AdapterBinding,
            context.Plan,
            activation,
            controlOutcome);
        if (pruning.Status != GovernedLoopSequentialFrontierTransitionStatus.Applied)
        {
            return null;
        }

        var now = Now(run);
        var skipEvents = new List<CustomLoopRunEvent>();
        var skipReferences = new List<GovernedLoopSequentialSkipEvidenceReference>();
        foreach (var pruned in pruning.Activations)
        {
            var owner = run with { Events = [.. run.Events, .. skipEvents] };
            var skipped = Event(
                owner,
                now,
                CustomLoopRunEventKind.TopologyNodeSkipped,
                $"Activation `{pruned.Activation.ActivationOrdinal}` was pruned by exact skipped edge `{pruned.GoverningControlEdgeId}`.",
                pruned.Activation.CycleIteration,
                pruned.Activation.NodeId);
            skipped = WithSequentialSkipEvidence(skipped, context.Anchor.AdapterBinding, pruned);
            skipEvents.Add(skipped);
            skipReferences.Add(new GovernedLoopSequentialSkipEvidenceReference(
                pruned.Activation.ActivationOrdinal,
                pruned.GoverningActivationOrdinal,
                pruned.GoverningControlEdgeId,
                skipped.EventId,
                skipped.SequentialNodeEvidence!.OutcomeArtifactHash));
        }

        var completed = GovernedLoopSequentialFrontierMachine.CompleteRunning(
            run.Frontier,
            context.Anchor.AdapterBinding,
            context.Plan,
            node,
            activation,
            attempt,
            attemptOperationId,
            evidence.EventId,
            sequentialEvidence.OutcomeArtifactHash,
            controlOutcome,
            skipReferences,
            now,
            FindCycleStartedAtUtc(run, activation.CycleId));
        return completed.Status == GovernedLoopSequentialFrontierTransitionStatus.Applied
            ? new SequentialFrontierCompletion(completed.Frontier!, skipEvents)
            : null;
    }

    private static DateTimeOffset? FindCycleStartedAtUtc(CustomLoopRunRecord run, string? cycleId)
        => cycleId is null
            ? null
            : run.Events
                .Where(item => string.Equals(item.SequentialNodeEvidence?.CycleId, cycleId, StringComparison.Ordinal)
                    && item.SequentialNodeEvidence?.CycleIteration == 1)
                .Select(item => (DateTimeOffset?)item.TimestampUtc)
                .Min();

    private async Task<RunAdvance> DispatchAndAdvanceSequentialInferenceAsync(
        SequentialExecutionContext context,
        CustomLoopRunRecord run,
        GovernedLoopSequentialPlanNode node,
        int attempt,
        string attemptOperationId,
        CustomLoopInferenceStep step,
        string actor,
        ProviderDispatchState dispatchState,
        CancellationToken cancellationToken)
    {
        var prepared = await DispatchSequentialNodeAsync(
            context,
            run,
            node,
            attempt,
            actor,
            token => PrepareOrExecuteSequentialInferenceAsync(context, node, attempt, attemptOperationId, run, step, actor, dispatchState, token),
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

        var frontier = CompleteSequentialFrontier(prepared.Run!, context, node, attempt, attemptOperationId);
        if (frontier is null)
        {
            var terminal = await TerminateAsync(prepared.Run!, actor, CustomLoopRunStatus.NeedsReview, "canonical_frontier_advancement_failed", "Canonical inference evidence resolved, but it could not advance the exact Running frontier.");
            return new RunAdvance(terminal.Run, terminal);
        }

        return await CommitCheckpointAsync(prepared.Run!, prepared.PendingCheckpoint, $"Inference checkpoint committed after `{step.Id}`.", frontier.Frontier, frontier.SkipEvents);
    }

    private async Task<RunAdvance> PrepareOrExecuteSequentialInferenceAsync(
        SequentialExecutionContext context,
        GovernedLoopSequentialPlanNode node,
        int attempt,
        string attemptOperationId,
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
                new SequentialNodeExecutionContext(
                    context.Anchor.AdapterBinding,
                    context.Artifact,
                    node,
                    RequireRunningSequentialActivation(run, node, attempt, attemptOperationId),
                    attempt,
                    attemptOperationId,
                    context.AllowedCapabilityIds,
                    context.AuditRecorder));
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
            new SequentialNodeExecutionContext(context.Anchor.AdapterBinding, context.Artifact, node, RequireRunningSequentialActivation(run, node, attempt, attemptOperationId), attempt, attemptOperationId, context.AllowedCapabilityIds, context.AuditRecorder));
    }

    private async Task<RunAdvance> DispatchAndAdvanceSequentialPureNodeAsync(
        SequentialExecutionContext context,
        CustomLoopRunRecord run,
        GovernedLoopSequentialPlanNode node,
        int attempt,
        string attemptOperationId,
        string actor,
        CancellationToken cancellationToken)
    {
        var claimedActivation = RequireRunningSequentialActivation(run, node, attempt, attemptOperationId);
        var prepared = await DispatchSequentialNodeAsync(
            context,
            run,
            node,
            attempt,
            actor,
            _ => PrepareOrExecuteSequentialPureNodeAsync(context, node, attempt, attemptOperationId, run, actor, cancellationToken),
            IntegrityToken());
        if (prepared.Terminal is not null)
        {
            return prepared;
        }

        if (prepared.PendingCheckpoint is null)
        {
            if (HasCommittedSequentialPureCheckpoint(prepared.Run!, node, claimedActivation, attempt))
            {
                return await ReconcileCommittedSequentialPureCheckpointAsync(
                    prepared.Run!,
                    actor,
                    context,
                    context.AuditRecorder,
                    prepared.AuthenticatedPureOutcomeSnapshot);
            }

            var terminal = await TerminateAsync(prepared.Run!, actor, CustomLoopRunStatus.NeedsReview, "canonical_pure_checkpoint_missing", "Canonical pure-node evidence resolved without one checkpoint advancement.");
            return new RunAdvance(terminal.Run, terminal);
        }

        var frontier = CompleteSequentialFrontier(prepared.Run!, context, node, attempt, attemptOperationId);
        if (frontier is null)
        {
            var terminal = await TerminateAsync(prepared.Run!, actor, CustomLoopRunStatus.NeedsReview, "canonical_frontier_advancement_failed", "Canonical pure-node evidence could not advance the exact Running frontier.");
            return new RunAdvance(terminal.Run, terminal);
        }

        return await CommitSequentialPureCheckpointAsync(
            prepared.Run!,
            prepared.PendingCheckpoint,
            $"Pure-node checkpoint committed after `{node.NodeId}`.",
            frontier.Frontier,
            frontier.SkipEvents,
            node,
            claimedActivation,
            attempt,
            actor,
            context.AuditRecorder);
    }

    private async Task<RunAdvance> CommitSequentialPureCheckpointAsync(
        CustomLoopRunRecord run,
        CustomLoopRunCheckpoint checkpoint,
        string detail,
        GovernedLoopFrontierPosture frontier,
        IReadOnlyList<CustomLoopRunEvent> skipEvents,
        GovernedLoopSequentialPlanNode node,
        GovernedLoopNodeExecutionEvidence activation,
        int attempt,
        string actor,
        IGovernedLoopSequentialAuditRecorder auditRecorder)
    {
        var durableOutcome = FindSequentialNodeEvidence(run, node, activation, attempt);
        if (durableOutcome?.SequentialNodeEvidence is not
            {
                Kind: CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
                Disposition: CustomLoopSequentialNodeDisposition.Completed,
            })
        {
            return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.InvalidState, run, "A pure-node checkpoint requires one exact durable completed outcome."));
        }

        for (var writeAttempt = 0; writeAttempt < 3; writeAttempt++)
        {
            var now = Now(run);
            var checkpointOwner = run with { Events = [.. run.Events, .. skipEvents] };
            var checkpointEvent = Event(checkpointOwner, now, CustomLoopRunEventKind.CheckpointCommitted, detail, checkpoint.Iteration);
            var committedCheckpoint = checkpoint with { LastCommittedSequence = checkpointEvent.Sequence };
            var candidate = Append(run, now, [.. skipEvents, checkpointEvent]) with
            {
                Checkpoint = committedCheckpoint,
                ExecutionClock = AdvanceClock(run.ExecutionClock, now, terminal: false),
                Frontier = frontier,
            };
            var persisted = await PersistAsync(run, candidate, IntegrityToken(), outcomeMayExist: false);
            if (persisted.Terminal is null)
            {
                return await HonorPureCheckpointControlAsync(persisted.Run!, actor, auditRecorder);
            }

            if (persisted.Terminal.Status != CustomLoopOrderedRunStatus.Conflict)
            {
                return persisted;
            }

            CustomLoopRunRecord? latest;
            try
            {
                latest = await _runStore.GetAsync(run.Id, IntegrityToken());
            }
            catch (Exception exception)
            {
                return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.NeedsReview, run, $"The pure-node checkpoint conflicted with lifecycle control, and the exact successor could not be loaded: {SafeExceptionClass(exception)}."));
            }

            if (latest is null)
            {
                return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.NotFound, null, "The pure-node checkpoint conflicted and the durable run disappeared."));
            }

            var latestOutcome = FindSequentialNodeEvidence(latest, node, activation, attempt);
            if (!IsAcceptedPureControlSuccessor(run, latest)
                || latestOutcome is null
                || !string.Equals(latestOutcome.EventId, durableOutcome.EventId, StringComparison.Ordinal)
                || !string.Equals(latestOutcome.SequentialNodeEvidence?.EvidenceHash, durableOutcome.SequentialNodeEvidence.EvidenceHash, StringComparison.Ordinal)
                || !string.Equals(latest.Frontier?.Payload.ContentHash, run.Frontier?.Payload.ContentHash, StringComparison.Ordinal))
            {
                return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.Conflict, latest, "The pure-node checkpoint conflicted with a successor outside the exact pause/cancel control protocol; no checkpoint was replayed."));
            }

            run = latest;
        }

        return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.Conflict, run, "The bounded pure-node checkpoint control reconciliation budget was exhausted."));
    }

    private async Task<RunAdvance> HonorPureCheckpointControlAsync(
        CustomLoopRunRecord run,
        string actor,
        IGovernedLoopSequentialAuditRecorder auditRecorder)
    {
        if (run.Status == CustomLoopRunStatus.PauseRequested)
        {
            return await PauseAtBoundaryAsync(run, actor);
        }

        if (run.Status == CustomLoopRunStatus.CancelRequested)
        {
            var cancelled = await TerminateAsync(
                run,
                actor,
                CustomLoopRunStatus.Cancelled,
                null,
                CanonicalDurableCancellationDetail,
                terminalAuditRecorder: auditRecorder);
            return new RunAdvance(cancelled.Run, cancelled);
        }

        return run.Status == CustomLoopRunStatus.Running
            ? new RunAdvance(run, null)
            : new RunAdvance(null, Result(CustomLoopOrderedRunStatus.InvalidState, run, $"The pure-node checkpoint cannot continue from {run.Status}."));
    }

    private async Task<RunAdvance> ReconcileCommittedSequentialPureCheckpointAsync(
        CustomLoopRunRecord run,
        string actor,
        SequentialExecutionContext context,
        IGovernedLoopSequentialAuditRecorder auditRecorder,
        CustomLoopRunRecord? authenticatedOutcomeSnapshot)
    {
        if (authenticatedOutcomeSnapshot is null
            || !TryCreateAuthenticatedPureCheckpointSnapshot(
                authenticatedOutcomeSnapshot,
                run,
                context,
                out var checkpointSnapshot))
        {
            if (run.IsTerminal)
            {
                var terminal = await CompleteOrProjectSequentialTerminalAsync(run, actor, context);
                return new RunAdvance(terminal.Run, terminal);
            }

            return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.Conflict, run, "The committed pure-node checkpoint is followed by execution progress outside this worker's authenticated checkpoint boundary."));
        }

        if (CustomLoopRunValidator.HasSameDurableVersion(checkpointSnapshot, run)
            || IsAcceptedPureControlSuccessor(checkpointSnapshot, run))
        {
            return await HonorPureCheckpointControlAsync(run, actor, auditRecorder);
        }

        if (TryGetAcceptedPurePausedLifecycleSuccessor(checkpointSnapshot, run))
        {
            return new RunAdvance(run, Result(CustomLoopOrderedRunStatus.Paused, run, "The exact pure-node checkpoint is durable and the run is paused."));
        }

        if (run.IsTerminal)
        {
            if (TryGetAcceptedPureCancellationLifecycleSuccessor(
                    checkpointSnapshot,
                    run,
                    out var cancellationLifecycle))
            {
                if (run.Events.LastOrDefault() is { Kind: CustomLoopRunEventKind.IntegrityWarning } warning)
                {
                    return new RunAdvance(run, Result(CustomLoopOrderedRunStatus.Cancelled, run, warning.Detail));
                }

                var cancellation = await CompleteSequentialPureTerminalLifecycleAuditAsync(
                    run,
                    CustomLoopRunStatus.Cancelled,
                    null,
                    cancellationLifecycle!.Detail,
                    cancellationLifecycle,
                    auditRecorder);
                return new RunAdvance(cancellation.Run, cancellation);
            }

            var terminal = await CompleteOrProjectSequentialTerminalAsync(run, actor, context);
            return new RunAdvance(terminal.Run, terminal);
        }

        return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.Conflict, run, $"The committed pure-node checkpoint is followed by {run.Status} execution progress outside this worker's authenticated boundary."));
    }

    private static bool HasCommittedSequentialPureCheckpoint(
        CustomLoopRunRecord run,
        GovernedLoopSequentialPlanNode node,
        GovernedLoopNodeExecutionEvidence activation,
        int attempt)
    {
        if (!CustomLoopRunValidator.Validate(run).IsValid
            || FindSequentialNodeEvidence(run, node, activation, attempt) is not
            {
                Kind: CustomLoopRunEventKind.NodeAttemptCompleted,
                SequentialNodeEvidence:
                {
                    Kind: CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
                    Disposition: CustomLoopSequentialNodeDisposition.Completed,
                } outcomeEvidence,
            } outcome
            || run.Frontier?.Payload.Nodes.SingleOrDefault(item => string.Equals(item.NodeId, node.NodeId, StringComparison.Ordinal)) is not
            {
                Status: GovernedLoopNodeExecutionStatus.Completed,
                Attempt: { } frontierAttempt,
                OutcomeEvidenceId: { } outcomeEvidenceId,
                OutcomeEvidenceHash: { } outcomeEvidenceHash,
            } frontierNode
            || frontierAttempt != attempt
            || !Equals(frontierNode.Descriptor, node.Descriptor)
            || !string.Equals(outcomeEvidenceId, outcome.EventId, StringComparison.Ordinal)
            || !string.Equals(outcomeEvidenceHash, outcomeEvidence.OutcomeArtifactHash, StringComparison.Ordinal))
        {
            return false;
        }

        return run.Events.Any(item => item.Kind == CustomLoopRunEventKind.CheckpointCommitted
            && item.Sequence > outcome.Sequence
            && item.Sequence <= run.Checkpoint.LastCommittedSequence);
    }

    private bool TryCreateAuthenticatedPureCheckpointSnapshot(
        CustomLoopRunRecord outcomeSnapshot,
        CustomLoopRunRecord latest,
        SequentialExecutionContext context,
        out CustomLoopRunRecord checkpointSnapshot)
    {
        checkpointSnapshot = outcomeSnapshot;
        if (!CustomLoopRunValidator.HasExactDurableEventPrefix(outcomeSnapshot, latest)
            || outcomeSnapshot.Events.LastOrDefault() is not
            {
                Kind: CustomLoopRunEventKind.NodeAttemptCompleted,
                PureNodeOutcomeJson: { } outcomeJson,
                SequentialNodeEvidence:
                {
                    Kind: CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
                    Disposition: CustomLoopSequentialNodeDisposition.Completed,
                } evidence,
            }
            || !GovernedLoopPureNodeOutcome.TryDeserialize(context.Artifact.Graph, outcomeJson, out var outcome, out _)
            || context.Plan.Nodes.SingleOrDefault(item => string.Equals(item.NodeId, evidence.NodeId, StringComparison.Ordinal)) is not { } node
            || outcomeSnapshot.Frontier?.Payload.Nodes.SingleOrDefault(item => string.Equals(item.NodeId, evidence.NodeId, StringComparison.Ordinal)) is not
            {
                AttemptOperationId: { } attemptOperationId,
            }
            || evidence.Attempt <= 0
            || !TryProjectPureNodeCheckpoint(outcomeSnapshot, context, node, outcome!, out var projectedCheckpoint, out _)
            || CompleteSequentialFrontier(outcomeSnapshot, context, node, evidence.Attempt!.Value, attemptOperationId) is not { } frontier
            || latest.Events.Skip(outcomeSnapshot.Events.Length).FirstOrDefault() is not
            {
                Kind: CustomLoopRunEventKind.CheckpointCommitted,
            } checkpointEvent
            || checkpointEvent.Sequence != outcomeSnapshot.Events.Length + 1L
            || outcomeSnapshot.LifecycleVersion == int.MaxValue)
        {
            return false;
        }

        checkpointSnapshot = outcomeSnapshot with
        {
            LifecycleVersion = outcomeSnapshot.LifecycleVersion + 1,
            UpdatedAtUtc = checkpointEvent.TimestampUtc,
            Checkpoint = projectedCheckpoint with { LastCommittedSequence = checkpointEvent.Sequence },
            ExecutionClock = AdvanceClock(outcomeSnapshot.ExecutionClock, checkpointEvent.TimestampUtc, terminal: false),
            Events = [.. outcomeSnapshot.Events, checkpointEvent],
            Frontier = frontier.Frontier,
        };
        return CustomLoopRunValidator.ValidateUpdate(outcomeSnapshot, checkpointSnapshot).IsValid
            && CustomLoopRunValidator.HasExactDurableEventPrefix(checkpointSnapshot, latest)
            && CheckpointsEqual(checkpointSnapshot.Checkpoint, latest.Checkpoint);
    }

    private static bool TryCreateConcurrentPureOutcomeSnapshot(
        CustomLoopRunRecord current,
        CustomLoopRunRecord latest,
        CustomLoopRunEvent retainedOutcome,
        out CustomLoopRunRecord outcomeSnapshot)
    {
        if (retainedOutcome.Kind != CustomLoopRunEventKind.NodeAttemptCompleted
            || retainedOutcome.SequentialNodeEvidence is not
            {
                Kind: CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
                Disposition: CustomLoopSequentialNodeDisposition.Completed,
            }
            || !TryCreateConcurrentPureEvidenceSnapshot(current, latest, retainedOutcome, out outcomeSnapshot)
            || outcomeSnapshot.Status is not (CustomLoopRunStatus.Running or CustomLoopRunStatus.PauseRequested))
        {
            outcomeSnapshot = current;
            return false;
        }

        return true;
    }

    private static bool TryCreateConcurrentPureRejectionSnapshot(
        CustomLoopRunRecord current,
        CustomLoopRunRecord latest,
        CustomLoopRunEvent retainedRejection,
        out CustomLoopRunRecord rejectionSnapshot)
    {
        if (retainedRejection.Kind != CustomLoopRunEventKind.NodeAttemptFailed
            || retainedRejection.SequentialNodeEvidence is not
            {
                Kind: CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection,
                Disposition: CustomLoopSequentialNodeDisposition.Rejected,
            }
            || !TryCreateConcurrentPureEvidenceSnapshot(current, latest, retainedRejection, out rejectionSnapshot)
            || rejectionSnapshot.Status is not (CustomLoopRunStatus.Running or CustomLoopRunStatus.PauseRequested or CustomLoopRunStatus.CancelRequested))
        {
            rejectionSnapshot = current;
            return false;
        }

        return true;
    }

    private static bool TryCreateConcurrentPureEvidenceSnapshot(
        CustomLoopRunRecord current,
        CustomLoopRunRecord latest,
        CustomLoopRunEvent retainedEvidence,
        out CustomLoopRunRecord evidenceSnapshot)
    {
        evidenceSnapshot = current;
        var precedingCount = retainedEvidence.Sequence - current.Events.Length - 1L;
        if (precedingCount is < 0 or > 2
            || retainedEvidence.Sequence > latest.Events.Length
            || current.LifecycleVersion == int.MaxValue
            || !TryCreatePureEvidencePredecessor(
                current,
                latest.Events.Skip(current.Events.Length).Take((int)precedingCount).ToArray(),
                out var predecessor)
            || predecessor.LifecycleVersion == int.MaxValue
            || retainedEvidence.Sequence != predecessor.Events.Length + 1L)
        {
            return false;
        }

        evidenceSnapshot = predecessor with
        {
            LifecycleVersion = predecessor.LifecycleVersion + 1,
            UpdatedAtUtc = retainedEvidence.TimestampUtc,
            Events = [.. predecessor.Events, retainedEvidence],
        };
        return CustomLoopRunValidator.ValidateUpdate(predecessor, evidenceSnapshot).IsValid
            && evidenceSnapshot.Status == predecessor.Status
            && CheckpointsEqual(predecessor.Checkpoint, evidenceSnapshot.Checkpoint)
            && Equals(predecessor.ExecutionClock, evidenceSnapshot.ExecutionClock)
            && string.Equals(predecessor.Frontier?.Payload.ContentHash, evidenceSnapshot.Frontier?.Payload.ContentHash, StringComparison.Ordinal)
            && predecessor.CompletedAtUtc == evidenceSnapshot.CompletedAtUtc
            && string.Equals(predecessor.FinalOutput, evidenceSnapshot.FinalOutput, StringComparison.Ordinal)
            && string.Equals(predecessor.FailureCode, evidenceSnapshot.FailureCode, StringComparison.Ordinal)
            && string.Equals(predecessor.FailureDetail, evidenceSnapshot.FailureDetail, StringComparison.Ordinal);
    }

    private static bool TryCreatePureEvidencePredecessor(
        CustomLoopRunRecord current,
        IReadOnlyList<CustomLoopRunEvent> precedingControls,
        out CustomLoopRunRecord predecessor)
    {
        predecessor = current;
        if (precedingControls.Count == 0)
        {
            return true;
        }

        CustomLoopRunStatus[][] possibleChains = (current.Status, precedingControls.Count) switch
        {
            (CustomLoopRunStatus.Running, 1) =>
            [
                [CustomLoopRunStatus.PauseRequested],
                [CustomLoopRunStatus.CancelRequested],
            ],
            (CustomLoopRunStatus.Running, 2) =>
            [
                [CustomLoopRunStatus.PauseRequested, CustomLoopRunStatus.CancelRequested],
            ],
            (CustomLoopRunStatus.PauseRequested, 1) =>
            [
                [CustomLoopRunStatus.CancelRequested],
            ],
            _ => [],
        };
        foreach (var statuses in possibleChains)
        {
            if (current.LifecycleVersion > int.MaxValue - statuses.Length)
            {
                continue;
            }

            var candidate = current with
            {
                LifecycleVersion = checked(current.LifecycleVersion + statuses.Length),
                Status = statuses[^1],
                UpdatedAtUtc = precedingControls[^1].TimestampUtc,
                Events = [.. current.Events, .. precedingControls],
            };
            if (IsAcceptedPureLifecycleChain(current, candidate, statuses))
            {
                predecessor = candidate;
                return true;
            }
        }

        return false;
    }

    private async Task<RunAdvance> PrepareOrExecuteSequentialPureNodeAsync(
        SequentialExecutionContext context,
        GovernedLoopSequentialPlanNode node,
        int attempt,
        string attemptOperationId,
        CustomLoopRunRecord run,
        string actor,
        CancellationToken cancellationToken)
    {
        var activation = RequireRunningSequentialActivation(run, node, attempt, attemptOperationId);
        var retained = FindSequentialNodeEvidence(run, node, activation, attempt);
        if (retained?.SequentialNodeEvidence is
            {
                Kind: CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
                Disposition: CustomLoopSequentialNodeDisposition.Completed,
            })
        {
            var retainedStartAuditFailure = await ReconcilePureNodeStartAuditAsync(run, node, activation, attempt, attemptOperationId, actor, context.AuditRecorder);
            if (retainedStartAuditFailure is not null)
            {
                var invalid = await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, retainedStartAuditFailure.FailureCode, retainedStartAuditFailure.Detail);
                return new RunAdvance(invalid.Run, invalid);
            }

            if (retained.Kind != CustomLoopRunEventKind.NodeAttemptCompleted
                || retained.PureNodeOutcomeJson is null
                || !GovernedLoopPureNodeOutcome.TryDeserialize(context.Artifact.Graph, retained.PureNodeOutcomeJson, out var retainedOutcome, out _)
                || !string.Equals(retainedOutcome!.NodeId, node.NodeId, StringComparison.Ordinal))
            {
                var invalid = await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, "canonical_pure_outcome_reconciliation_failed", "The retained pure-node outcome is incomplete, divergent, or not bound to the exact graph node; automatic evaluation is forbidden.");
                return new RunAdvance(invalid.Run, invalid);
            }

            var retainedResolution = GovernedLoopSequentialBindingResolver.Resolve(context.Artifact, context.Plan, node, activation, run);
            if (!retainedResolution.IsResolved
                || !PureNodeInputsEqual(retainedResolution.Inputs, retainedOutcome.Inputs)
                || !TryProjectPureNodeCheckpoint(run, context, node, retainedOutcome, out var retainedCheckpoint, out _))
            {
                var invalid = await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, "canonical_pure_outcome_reconciliation_failed", "The retained pure-node outcome does not match the exact durable source evidence or legacy checkpoint projection; automatic evaluation is forbidden.");
                return new RunAdvance(invalid.Run, invalid);
            }

            var auditFailure = await AppendOutcomeAuditAsync(
                run,
                retained,
                CreatePureNodeAudit(run, actor, retained, AuditSchema.Outcomes.Succeeded, retainedOutcome),
                context.AuditRecorder,
                IntegrityToken());
            if (auditFailure is not null)
            {
                var invalid = await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, auditFailure.FailureCode, auditFailure.Detail);
                return new RunAdvance(invalid.Run, invalid);
            }

            return new RunAdvance(run, null, retainedCheckpoint);
        }

        if (retained?.SequentialNodeEvidence is
            {
                Kind: CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection,
                Disposition: CustomLoopSequentialNodeDisposition.Rejected,
            })
        {
            var retainedStartAuditFailure = await ReconcilePureNodeStartAuditAsync(run, node, activation, attempt, attemptOperationId, actor, context.AuditRecorder);
            if (retainedStartAuditFailure is not null)
            {
                var invalid = await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, retainedStartAuditFailure.FailureCode, retainedStartAuditFailure.Detail);
                return new RunAdvance(invalid.Run, invalid);
            }

            var auditFailure = await AppendOutcomeAuditAsync(
                run,
                retained,
                CreatePureNodeAudit(run, actor, retained, AuditSchema.Outcomes.Failed, null),
                context.AuditRecorder,
                IntegrityToken());
            if (auditFailure is not null)
            {
                var invalid = await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, auditFailure.FailureCode, auditFailure.Detail);
                return new RunAdvance(invalid.Run, invalid);
            }

            if (run.Status == CustomLoopRunStatus.CancelRequested || IsCanonicalCancellationRejection(retained))
            {
                return await TerminateSequentialPureRejectionAsync(
                    run,
                    actor,
                    CustomLoopRunStatus.Cancelled,
                    null,
                    run.Status == CustomLoopRunStatus.CancelRequested ? CanonicalDurableCancellationDetail : retained.Detail,
                    retained,
                    context.AuditRecorder);
            }

            if (!TryReadPureNodeRejection(retained.Detail, out var retainedFailureCode, out var retainedFailureDetail))
            {
                var invalid = await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, "canonical_pure_rejection_classification_missing", "The retained pure-node rejection does not authenticate one exact bounded failure classification; automatic terminal replay is forbidden.");
                return new RunAdvance(invalid.Run, invalid);
            }

            return await TerminateSequentialPureRejectionAsync(
                run,
                actor,
                CustomLoopRunStatus.Failed,
                retainedFailureCode,
                retainedFailureDetail,
                retained,
                context.AuditRecorder);
        }

        var sequentialNode = new SequentialNodeExecutionContext(
            context.Anchor.AdapterBinding,
            context.Artifact,
            node,
            activation,
            attempt,
            attemptOperationId,
            context.AllowedCapabilityIds,
            context.AuditRecorder);
        var started = FindSequentialDispatchStart(run, node, activation, attempt, attemptOperationId);
        if (started is null)
        {
            var invalid = await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, "canonical_pure_start_missing", "The Running pure-node frontier has no exact atomic start marker; automatic evaluation is forbidden.");
            return new RunAdvance(invalid.Run, invalid);
        }

        var startAuditFailure = await ReconcilePureNodeStartAuditAsync(run, node, activation, attempt, attemptOperationId, actor, context.AuditRecorder);
        if (startAuditFailure is not null)
        {
            return await RejectSequentialPureNodeAsync(run, actor, sequentialNode, startAuditFailure.FailureCode, startAuditFailure.Detail);
        }

        var controlBoundary = await RefreshPureControlUpdateAsync(run);
        if (controlBoundary.Terminal is not null)
        {
            return controlBoundary;
        }

        run = controlBoundary.Run!;
        if (run.Status == CustomLoopRunStatus.CancelRequested)
        {
            return await RejectSequentialPureNodeAsync(run, actor, sequentialNode, null, CanonicalDurableCancellationDetail, CustomLoopRunStatus.Cancelled);
        }

        if (run.Status is not (CustomLoopRunStatus.Running or CustomLoopRunStatus.PauseRequested))
        {
            return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.InvalidState, run, $"Deterministic pure-node execution cannot continue from {run.Status}."));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return await RejectSequentialPureNodeAsync(run, actor, sequentialNode, null, CanonicalPureNodeCancellationDetail, CustomLoopRunStatus.Cancelled);
        }

        var resolution = GovernedLoopSequentialBindingResolver.Resolve(context.Artifact, context.Plan, node, sequentialNode.Activation, run);
        if (!resolution.IsResolved)
        {
            return await RejectSequentialPureNodeAsync(
                run,
                actor,
                sequentialNode,
                resolution.FailureCode ?? "pure_node_binding_invalid",
                $"Pure-node input resolution rejected the exact binding at `{resolution.FailurePath ?? "$"}`.");
        }

        if (!GovernedLoopPureNodeEvaluator.TryEvaluate(
                context.Artifact.Graph,
                node.NodeId,
                resolution.Inputs,
                out var output,
                out var validationEvidence,
                out var evaluation))
        {
            var first = evaluation.Errors.FirstOrDefault();
            return await RejectSequentialPureNodeAsync(
                run,
                actor,
                sequentialNode,
                first?.Code ?? "pure_node_outcome_invalid",
                $"Pure-node execution was rejected at `{first?.Path ?? "$"}` by its exact bounded contract.");
        }

        if (!GovernedLoopPureNodeOutcome.TryCreate(
                context.Artifact.Graph,
                node.NodeId,
                resolution.Inputs,
                [output!],
                validationEvidence,
                out var outcome,
                out var outcomeValidation))
        {
            var first = outcomeValidation.Errors.FirstOrDefault();
            return await RejectSequentialPureNodeAsync(
                run,
                actor,
                sequentialNode,
                first?.Code ?? "pure_node_outcome_invalid",
                $"Pure-node outcome creation was rejected at `{first?.Path ?? "$"}` by its exact bounded contract.");
        }

        if (!TryProjectPureNodeCheckpoint(run, context, node, outcome!, out var projectedCheckpoint, out var projectionFailure))
        {
            return await RejectSequentialPureNodeAsync(
                run,
                actor,
                sequentialNode,
                "pure_node_legacy_bridge_invalid",
                projectionFailure ?? "The pure-node output cannot be represented by the bounded legacy checkpoint projection.");
        }

        var persistedOutcome = await PersistSequentialPureOutcomeAsync(run, actor, sequentialNode, outcome!);
        if (persistedOutcome.Terminal is not null)
        {
            return persistedOutcome;
        }

        run = persistedOutcome.Run!;
        var authenticatedOutcomeSnapshot = persistedOutcome.AuthenticatedPureOutcomeSnapshot;
        var durableCompletion = FindSequentialNodeEvidence(run, node, sequentialNode.Activation, attempt);
        if (durableCompletion is not
            {
                Kind: CustomLoopRunEventKind.NodeAttemptCompleted,
                SequentialNodeEvidence:
                {
                    Kind: CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
                    Disposition: CustomLoopSequentialNodeDisposition.Completed,
                },
                PureNodeOutcomeJson: { } durableOutcomeJson,
            }
            || !string.Equals(durableOutcomeJson, outcome!.CanonicalJson, StringComparison.Ordinal))
        {
            var invalid = await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, "canonical_pure_outcome_reconciliation_failed", "The deterministic pure-node completion write returned without its exact authenticated outcome.");
            return new RunAdvance(invalid.Run, invalid);
        }

        var completionAuditFailure = await AppendOutcomeAuditAsync(
            run,
            durableCompletion,
            CreatePureNodeAudit(run, actor, durableCompletion, AuditSchema.Outcomes.Succeeded, outcome),
            context.AuditRecorder,
            IntegrityToken());
        if (completionAuditFailure is not null)
        {
            var invalid = await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, completionAuditFailure.FailureCode, completionAuditFailure.Detail);
            return new RunAdvance(invalid.Run, invalid);
        }

        return HasCommittedSequentialPureCheckpoint(run, node, sequentialNode.Activation, attempt)
            ? new RunAdvance(
                run,
                null,
                AuthenticatedPureOutcomeSnapshot: authenticatedOutcomeSnapshot)
            : new RunAdvance(
                run,
                null,
                projectedCheckpoint,
                AuthenticatedPureOutcomeSnapshot: authenticatedOutcomeSnapshot);
    }

    private async Task<RunAdvance> PersistSequentialPureOutcomeAsync(
        CustomLoopRunRecord run,
        string actor,
        SequentialNodeExecutionContext sequentialNode,
        GovernedLoopPureNodeOutcome outcome)
    {
        for (var writeAttempt = 0; writeAttempt < 3; writeAttempt++)
        {
            var refreshed = await RefreshPureControlUpdateAsync(run);
            if (refreshed.Terminal is not null)
            {
                return refreshed;
            }

            run = refreshed.Run!;
            if (run.Status == CustomLoopRunStatus.CancelRequested)
            {
                return await RejectSequentialPureNodeAsync(run, actor, sequentialNode, null, CanonicalDurableCancellationDetail, CustomLoopRunStatus.Cancelled);
            }

            if (run.Status is not (CustomLoopRunStatus.Running or CustomLoopRunStatus.PauseRequested))
            {
                return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.InvalidState, run, $"Deterministic pure-node outcome persistence cannot continue from {run.Status}."));
            }

            var completion = Event(
                run,
                Now(run),
                CustomLoopRunEventKind.NodeAttemptCompleted,
                "Deterministic pure-node outcome was committed.",
                run.Checkpoint.Iteration,
                sequentialNode.Node.NodeId,
                sequentialNode.Attempt,
                pureNodeOutcomeJson: outcome.CanonicalJson);
            completion = WithSequentialEvidence(completion, sequentialNode, CustomLoopSequentialNodeEvidenceKind.CompletedOutcome, CustomLoopSequentialNodeDisposition.Completed);
            var persisted = await PersistAsync(run, Append(run, completion.TimestampUtc, [completion]), IntegrityToken(), outcomeMayExist: false);
            if (persisted.Terminal is null)
            {
                return new RunAdvance(
                    persisted.Run,
                    null,
                    AuthenticatedPureOutcomeSnapshot: persisted.Run);
            }

            if (persisted.Terminal.Status != CustomLoopOrderedRunStatus.Conflict)
            {
                return persisted;
            }

            CustomLoopRunRecord? latest;
            try
            {
                latest = await _runStore.GetAsync(run.Id, IntegrityToken());
            }
            catch (Exception exception)
            {
                return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.NeedsReview, run, $"The pure-node completion conflicted with lifecycle control, and the exact successor could not be loaded: {SafeExceptionClass(exception)}."));
            }

            if (latest is null)
            {
                return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.NotFound, null, "The pure-node completion conflicted and the durable run disappeared."));
            }

            var retained = FindSequentialNodeEvidence(latest, sequentialNode.Node, sequentialNode.Activation, sequentialNode.Attempt);
            if (retained is
                {
                    Kind: CustomLoopRunEventKind.NodeAttemptCompleted,
                    SequentialNodeEvidence:
                    {
                        Kind: CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
                        Disposition: CustomLoopSequentialNodeDisposition.Completed,
                    },
                }
                && string.Equals(retained.PureNodeOutcomeJson, outcome.CanonicalJson, StringComparison.Ordinal))
            {
                if (TryCreateConcurrentPureOutcomeSnapshot(run, latest, retained, out var outcomeSnapshot)
                    && CustomLoopRunValidator.HasExactDurableEventPrefix(outcomeSnapshot, latest)
                    && (CustomLoopRunValidator.HasSameDurableVersion(outcomeSnapshot, latest)
                        || IsAcceptedPureControlSuccessor(outcomeSnapshot, latest)
                        || HasCommittedSequentialPureCheckpoint(latest, sequentialNode.Node, sequentialNode.Activation, sequentialNode.Attempt)))
                {
                    return new RunAdvance(
                        latest,
                        null,
                        AuthenticatedPureOutcomeSnapshot: outcomeSnapshot);
                }

                return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.Conflict, latest, "The concurrent pure-node completion does not extend the exact open attempt through an authenticated outcome, control chain, or committed checkpoint."));
            }

            if (!IsAcceptedPureControlSuccessor(run, latest)
                || !HasExactOpenPureAttempt(latest, sequentialNode))
            {
                return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.Conflict, latest, "The pure-node completion conflicted with a successor outside the exact pause/cancel control protocol; no outcome was replayed."));
            }

            run = latest;
        }

        return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.Conflict, run, "The bounded pure-node outcome control reconciliation budget was exhausted."));
    }

    private static bool TryProjectPureNodeCheckpoint(
        CustomLoopRunRecord run,
        SequentialExecutionContext context,
        GovernedLoopSequentialPlanNode node,
        GovernedLoopPureNodeOutcome outcome,
        out CustomLoopRunCheckpoint checkpoint,
        out string? failure)
    {
        checkpoint = run.Checkpoint;
        failure = null;
        var textOutput = outcome.Outputs.SingleOrDefault(item => item.Value.Kind == EmbodySense.Core.Common.Loops.Models.Custom.Graph.GovernedLoopValueKind.Text && !item.Value.IsNull);
        if (textOutput is null)
        {
            return true;
        }

        string? text;
        try
        {
            text = JsonSerializer.Deserialize<string>(textOutput.Value.CanonicalValueJson);
        }
        catch (JsonException)
        {
            failure = "The canonical pure-node Text output could not be projected without reinterpretation.";
            return false;
        }

        if (text is null)
        {
            failure = "The canonical pure-node Text output cannot be null at the legacy checkpoint boundary.";
            return false;
        }

        var graph = context.Artifact.Graph;
        var targets = graph.Bindings
            .Where(binding => string.Equals(binding.FromNodeId, node.NodeId, StringComparison.Ordinal)
                && string.Equals(binding.FromPortId, textOutput.PortId, StringComparison.Ordinal))
            .Select(binding => graph.Nodes.Single(target => string.Equals(target.Id, binding.ToNodeId, StringComparison.Ordinal)).Descriptor)
            .ToArray();
        var feedsInference = targets.Any(descriptor => Equals(descriptor, GovernedLoopSequentialNodeDescriptors.ProviderInference));
        var feedsExit = targets.Any(descriptor => Equals(descriptor, GovernedLoopSequentialNodeDescriptors.SuccessExit));
        if (!feedsInference && !feedsExit)
        {
            return true;
        }

        if (text.Length > CustomLoopLimits.MaxCanonicalModelOutputCharacters)
        {
            failure = $"The pure-node Text output exceeds the {CustomLoopLimits.MaxCanonicalModelOutputCharacters}-character legacy inference/exit bridge bound.";
            return false;
        }

        var retained = new CustomLoopRetainedOutput(
            node.NodeId,
            run.Checkpoint.Iteration,
            text,
            CustomLoopTraceContentHash.Compute(text));
        var earlier = feedsInference && !run.Checkpoint.EarlierRetainedOutputs.Any(item => string.Equals(item.StepId, node.NodeId, StringComparison.Ordinal) && item.Iteration == run.Checkpoint.Iteration)
            ? [.. run.Checkpoint.EarlierRetainedOutputs, retained]
            : run.Checkpoint.EarlierRetainedOutputs;
        checkpoint = run.Checkpoint with
        {
            EarlierRetainedOutputs = earlier,
            CurrentIterationResult = feedsExit ? retained : run.Checkpoint.CurrentIterationResult,
        };
        return true;
    }

    private static bool PureNodeInputsEqual(
        IReadOnlyList<GovernedLoopTypedBindingValue> left,
        IReadOnlyList<GovernedLoopTypedBindingValue> right)
        => left.Count == right.Count
            && left.Zip(right).All(pair => pair.First.SchemaVersion == pair.Second.SchemaVersion
                && Equals(pair.First.GraphRevision, pair.Second.GraphRevision)
                && string.Equals(pair.First.BindingId, pair.Second.BindingId, StringComparison.Ordinal)
                && pair.First.BindingKind == pair.Second.BindingKind
                && string.Equals(pair.First.SourceNodeId, pair.Second.SourceNodeId, StringComparison.Ordinal)
                && string.Equals(pair.First.SourcePortId, pair.Second.SourcePortId, StringComparison.Ordinal)
                && string.Equals(pair.First.TargetNodeId, pair.Second.TargetNodeId, StringComparison.Ordinal)
                && string.Equals(pair.First.TargetPortId, pair.Second.TargetPortId, StringComparison.Ordinal)
                && string.Equals(pair.First.ValueSchemaId, pair.Second.ValueSchemaId, StringComparison.Ordinal)
                && pair.First.Value.Equals(pair.Second.Value));

    private async Task<RunAdvance> RejectSequentialPureNodeAsync(
        CustomLoopRunRecord run,
        string actor,
        SequentialNodeExecutionContext sequentialNode,
        string? failureCode,
        string detail,
        CustomLoopRunStatus terminalStatus = CustomLoopRunStatus.Failed)
    {
        var startAuditFailure = await ReconcilePureNodeStartAuditAsync(
            run,
            sequentialNode.Node,
            sequentialNode.Activation,
            sequentialNode.Attempt,
            sequentialNode.AttemptOperationId,
            actor,
            sequentialNode.AuditRecorder);
        if (startAuditFailure is not null)
        {
            var invalid = await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, startAuditFailure.FailureCode, startAuditFailure.Detail);
            return new RunAdvance(invalid.Run, invalid);
        }

        var durableFailureCode = failureCode is null ? null : BoundPureNodeFailureCode(failureCode);
        var terminalDetail = durableFailureCode is null ? detail : BoundPureNodeFailureDetail(durableFailureCode, detail);
        var durableDetail = durableFailureCode is null ? detail : WritePureNodeRejection(durableFailureCode, terminalDetail);
        var originallyRequestedDurableDetail = durableDetail;
        var effectiveTerminalStatus = terminalStatus;
        CustomLoopRunEvent? durableFailure = null;
        CustomLoopRunRecord? durableRejectionSnapshot = null;
        for (var writeAttempt = 0; writeAttempt < 3; writeAttempt++)
        {
            if (run.Status == CustomLoopRunStatus.CancelRequested)
            {
                durableFailureCode = null;
                terminalDetail = CanonicalDurableCancellationDetail;
                durableDetail = CanonicalDurableCancellationDetail;
                effectiveTerminalStatus = CustomLoopRunStatus.Cancelled;
            }

            var failed = Event(
                run,
                Now(run),
                CustomLoopRunEventKind.NodeAttemptFailed,
                durableDetail,
                run.Checkpoint.Iteration,
                sequentialNode.Node.NodeId,
                sequentialNode.Attempt);
            failed = WithSequentialEvidence(failed, sequentialNode, CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection, CustomLoopSequentialNodeDisposition.Rejected);
            var persisted = await PersistAsync(run, Append(run, failed.TimestampUtc, [failed]), IntegrityToken(), outcomeMayExist: false);
            if (persisted.Terminal is null)
            {
                run = persisted.Run!;
                durableFailure = run.Events.Single(item => string.Equals(item.EventId, failed.EventId, StringComparison.Ordinal));
                durableRejectionSnapshot = run;
                break;
            }

            if (persisted.Terminal.Status != CustomLoopOrderedRunStatus.Conflict)
            {
                return persisted;
            }

            CustomLoopRunRecord? latest;
            try
            {
                latest = await _runStore.GetAsync(run.Id, IntegrityToken());
            }
            catch (Exception exception)
            {
                return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.NeedsReview, run, $"The pure-node rejection conflicted with lifecycle control, and the exact successor could not be loaded: {SafeExceptionClass(exception)}."));
            }

            if (latest is null)
            {
                return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.NotFound, null, "The pure-node rejection conflicted and the durable run disappeared."));
            }

            var retained = FindSequentialNodeEvidence(latest, sequentialNode.Node, sequentialNode.Activation, sequentialNode.Attempt);
            if (retained?.SequentialNodeEvidence is
                {
                    Kind: CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection,
                    Disposition: CustomLoopSequentialNodeDisposition.Rejected,
                })
            {
                if (!TryCreateConcurrentPureRejectionSnapshot(run, latest, retained, out var rejectionSnapshot)
                    || !CustomLoopRunValidator.HasExactDurableEventPrefix(rejectionSnapshot, latest))
                {
                    return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.Conflict, latest, "The concurrent pure-node rejection does not preserve the exact open-attempt and lifecycle-control prefix."));
                }

                var exactRejection = string.Equals(retained.Detail, durableDetail, StringComparison.Ordinal)
                    || rejectionSnapshot.Status == CustomLoopRunStatus.CancelRequested
                        && (string.Equals(retained.Detail, originallyRequestedDurableDetail, StringComparison.Ordinal)
                            || string.Equals(retained.Detail, CanonicalDurableCancellationDetail, StringComparison.Ordinal));
                if (!exactRejection)
                {
                    return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.Conflict, latest, "The pure-node rejection identity is already bound to divergent terminal evidence."));
                }

                var requestedStatus = rejectionSnapshot.Status == CustomLoopRunStatus.CancelRequested
                    ? CustomLoopRunStatus.Cancelled
                    : effectiveTerminalStatus;
                var requestedDetail = requestedStatus == CustomLoopRunStatus.Cancelled
                    ? CanonicalDurableCancellationDetail
                    : terminalDetail;
                var acceptedTerminal = TryGetAcceptedPureRejectionTerminalSuccessor(
                    rejectionSnapshot,
                    latest,
                    requestedStatus,
                    requestedStatus == CustomLoopRunStatus.Failed ? durableFailureCode : null,
                    requestedDetail,
                    retained,
                    out _,
                    out _);
                if (!CustomLoopRunValidator.HasSameDurableVersion(rejectionSnapshot, latest)
                    && !IsAcceptedPureControlSuccessor(rejectionSnapshot, latest)
                    && !acceptedTerminal)
                {
                    return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.Conflict, latest, "The concurrent pure-node rejection does not extend through an authenticated control or terminal lifecycle chain."));
                }

                run = latest;
                durableFailure = retained;
                durableRejectionSnapshot = rejectionSnapshot;
                break;
            }

            if (!IsAcceptedPureControlSuccessor(run, latest)
                || !HasExactOpenPureAttempt(latest, sequentialNode))
            {
                return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.Conflict, latest, "The pure-node rejection conflicted with a successor outside the exact pause/cancel control protocol; no rejection was replayed."));
            }

            run = latest;
        }

        if (durableFailure is null || durableRejectionSnapshot is null)
        {
            return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.Conflict, run, "The bounded pure-node rejection control reconciliation budget was exhausted."));
        }

        var auditFailure = await AppendOutcomeAuditAsync(
            run,
            durableFailure,
            CreatePureNodeAudit(run, actor, durableFailure, AuditSchema.Outcomes.Failed, null),
            sequentialNode.AuditRecorder,
            IntegrityToken());
        if (auditFailure is not null)
        {
            if (run.IsTerminal)
            {
                return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.NeedsReview, run, auditFailure.Detail));
            }

            var review = await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, auditFailure.FailureCode, auditFailure.Detail);
            return new RunAdvance(review.Run, review);
        }

        if (run.IsTerminal)
        {
            var requestedStatus = durableRejectionSnapshot.Status == CustomLoopRunStatus.CancelRequested
                ? CustomLoopRunStatus.Cancelled
                : effectiveTerminalStatus;
            var requestedDetail = requestedStatus == CustomLoopRunStatus.Cancelled
                ? CanonicalDurableCancellationDetail
                : terminalDetail;
            if (!TryGetAcceptedPureRejectionTerminalSuccessor(
                    durableRejectionSnapshot,
                    run,
                    requestedStatus,
                    requestedStatus == CustomLoopRunStatus.Failed ? durableFailureCode : null,
                    requestedDetail,
                    durableFailure,
                    out var authenticatedStatus,
                    out var authenticatedLifecycle))
            {
                return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.Conflict, run, "The retained pure-node rejection terminal is not an exact authenticated successor of its durable rejection snapshot."));
            }

            if (run.Events.LastOrDefault() is { Kind: CustomLoopRunEventKind.IntegrityWarning } existingWarning)
            {
                var warnedStatus = authenticatedStatus == CustomLoopRunStatus.Cancelled
                    ? CustomLoopOrderedRunStatus.Cancelled
                    : authenticatedStatus == CustomLoopRunStatus.Failed
                        ? CustomLoopOrderedRunStatus.Failed
                        : CustomLoopOrderedRunStatus.NeedsReview;
                return new RunAdvance(run, Result(warnedStatus, run, existingWarning.Detail));
            }

            var reconciled = await CompleteSequentialPureTerminalLifecycleAuditAsync(
                run,
                authenticatedStatus,
                authenticatedStatus == CustomLoopRunStatus.Failed ? durableFailureCode : null,
                authenticatedLifecycle!.Detail,
                authenticatedLifecycle,
                sequentialNode.AuditRecorder);
            return new RunAdvance(reconciled.Run, reconciled);
        }

        var controlBoundary = await RefreshPureControlUpdateAsync(run);
        if (controlBoundary.Terminal is not null)
        {
            return controlBoundary;
        }

        run = controlBoundary.Run!;
        if (run.Status == CustomLoopRunStatus.CancelRequested)
        {
            effectiveTerminalStatus = CustomLoopRunStatus.Cancelled;
            terminalDetail = CanonicalDurableCancellationDetail;
        }

        return await TerminateSequentialPureRejectionAsync(
            run,
            actor,
            effectiveTerminalStatus,
            durableFailureCode,
            terminalDetail,
            durableFailure,
            sequentialNode.AuditRecorder);
    }

    private async Task<RunAdvance> TerminateSequentialPureRejectionAsync(
        CustomLoopRunRecord run,
        string actor,
        CustomLoopRunStatus requestedStatus,
        string? failureCode,
        string detail,
        CustomLoopRunEvent durableFailure,
        IGovernedLoopSequentialAuditRecorder auditRecorder)
    {
        for (var writeAttempt = 0; writeAttempt < 3; writeAttempt++)
        {
            var terminalStatus = run.Status == CustomLoopRunStatus.CancelRequested
                ? CustomLoopRunStatus.Cancelled
                : requestedStatus;
            if (terminalStatus == CustomLoopRunStatus.Cancelled && run.Status != CustomLoopRunStatus.CancelRequested)
            {
                var requested = await RequestSequentialPureCancellationAsync(run, actor, durableFailure, auditRecorder);
                if (requested.Terminal is not null)
                {
                    return requested;
                }

                run = requested.Run!;
                terminalStatus = CustomLoopRunStatus.Cancelled;
            }

            var terminalDetail = terminalStatus == CustomLoopRunStatus.Cancelled ? CanonicalDurableCancellationDetail : detail;
            var terminalFailureCode = terminalStatus == CustomLoopRunStatus.Failed ? failureCode : null;
            var terminal = await TerminateAsync(
                run,
                actor,
                terminalStatus,
                terminalFailureCode,
                terminalDetail,
                terminalOutcomeMayExist: false,
                terminalAuditRecorder: auditRecorder);
            if (terminal.Status != CustomLoopOrderedRunStatus.Conflict)
            {
                return new RunAdvance(terminal.Run, terminal);
            }

            CustomLoopRunRecord? latest;
            try
            {
                latest = await _runStore.GetAsync(run.Id, IntegrityToken());
            }
            catch (Exception exception)
            {
                return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.NeedsReview, run, $"The pure-node terminal lifecycle conflicted with control, and the exact successor could not be loaded: {SafeExceptionClass(exception)}."));
            }

            if (latest is null)
            {
                return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.NotFound, null, "The pure-node terminal lifecycle conflicted and the durable run disappeared."));
            }

            if (!HasExactRetainedPureRejection(latest, durableFailure))
            {
                return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.Conflict, latest, "The pure-node terminal lifecycle lost its exact durable rejection evidence."));
            }

            if (latest.IsTerminal)
            {
                if (!TryGetAcceptedPureRejectionTerminalSuccessor(
                        run,
                        latest,
                        terminalStatus,
                        terminalFailureCode,
                        terminalDetail,
                        durableFailure,
                        out var authenticatedStatus,
                        out var authenticatedLifecycle))
                {
                    return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.Conflict, latest, "The pure-node terminal lifecycle is already bound to a divergent or unauthenticated terminal disposition."));
                }

                if (latest.Events.LastOrDefault() is { Kind: CustomLoopRunEventKind.IntegrityWarning } terminalWarning)
                {
                    var warnedStatus = authenticatedStatus == CustomLoopRunStatus.Cancelled
                        ? CustomLoopOrderedRunStatus.Cancelled
                        : authenticatedStatus == CustomLoopRunStatus.Failed
                            ? CustomLoopOrderedRunStatus.Failed
                            : CustomLoopOrderedRunStatus.NeedsReview;
                    return new RunAdvance(latest, Result(warnedStatus, latest, terminalWarning.Detail));
                }

                var reconciled = await CompleteSequentialPureTerminalLifecycleAuditAsync(
                    latest,
                    authenticatedStatus,
                    authenticatedStatus == CustomLoopRunStatus.Failed ? terminalFailureCode : null,
                    authenticatedLifecycle!.Detail,
                    authenticatedLifecycle,
                    auditRecorder);
                return new RunAdvance(reconciled.Run, reconciled);
            }

            if (!IsAcceptedPureControlSuccessor(run, latest))
            {
                return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.Conflict, latest, "The pure-node terminal lifecycle conflicted with a successor outside the exact lifecycle-only control protocol."));
            }

            run = latest;
        }

        return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.Conflict, run, "The bounded pure-node terminal lifecycle reconciliation budget was exhausted."));
    }

    private async Task<RunAdvance> RequestSequentialPureCancellationAsync(
        CustomLoopRunRecord run,
        string actor,
        CustomLoopRunEvent durableFailure,
        IGovernedLoopSequentialAuditRecorder auditRecorder)
    {
        for (var writeAttempt = 0; writeAttempt < 3; writeAttempt++)
        {
            if (run.Status == CustomLoopRunStatus.CancelRequested)
            {
                return new RunAdvance(run, null);
            }

            if (run.Status is not (CustomLoopRunStatus.Running or CustomLoopRunStatus.PauseRequested))
            {
                return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.InvalidState, run, $"Pure-node cancellation cannot be requested from {run.Status}."));
            }

            var now = Now(run);
            var lifecycle = Event(run, now, CustomLoopRunEventKind.LifecycleChanged, "Cancellation was requested after deterministic pure-node rejection; no later node may start.");
            var candidate = Append(run, now, [lifecycle]) with { Status = CustomLoopRunStatus.CancelRequested };
            var persisted = await PersistAsync(run, candidate, IntegrityToken(), outcomeMayExist: false);
            if (persisted.Terminal is null)
            {
                try
                {
                    await _auditLog.AppendAsync(AuditEvent.Create(actor, AuditSchema.Actions.LoopRunLifecycle, persisted.Run!.Id, AuditSchema.Outcomes.Requested, "Pure-node cancellation request is durable.", RunMetadata(persisted.Run)), IntegrityToken());
                }
                catch
                {
                    // The durable cancellation request remains authoritative; terminal audit records any later integrity warning.
                }

                return persisted;
            }

            if (persisted.Terminal.Status != CustomLoopOrderedRunStatus.Conflict)
            {
                return persisted;
            }

            CustomLoopRunRecord? latest;
            try
            {
                latest = await _runStore.GetAsync(run.Id, IntegrityToken());
            }
            catch (Exception exception)
            {
                return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.NeedsReview, run, $"The pure-node cancellation request conflicted, and its successor could not be loaded: {SafeExceptionClass(exception)}."));
            }

            if (latest is null)
            {
                return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.NotFound, null, "The pure-node cancellation request conflicted and the durable run disappeared."));
            }

            var sameFailure = HasExactRetainedPureRejection(latest, durableFailure);
            if (sameFailure && TryGetAcceptedPureCancellationTerminalSuccessor(run, latest, durableFailure, out var cancellationLifecycle))
            {
                if (latest.Events.LastOrDefault() is { Kind: CustomLoopRunEventKind.IntegrityWarning } warning)
                {
                    return new RunAdvance(latest, Result(CustomLoopOrderedRunStatus.Cancelled, latest, warning.Detail));
                }

                var cancellation = await CompleteSequentialPureTerminalLifecycleAuditAsync(
                    latest,
                    CustomLoopRunStatus.Cancelled,
                    null,
                    cancellationLifecycle!.Detail,
                    cancellationLifecycle,
                    auditRecorder);
                return new RunAdvance(cancellation.Run, cancellation);
            }

            if (!sameFailure || !IsAcceptedPureControlSuccessor(run, latest))
            {
                return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.Conflict, latest, "The pure-node cancellation request conflicted with a successor outside the exact lifecycle-only control protocol."));
            }

            run = latest;
        }

        return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.Conflict, run, "The bounded pure-node cancellation-request reconciliation budget was exhausted."));
    }

    private static string BoundPureNodeFailureCode(string failureCode)
    {
        return !string.IsNullOrWhiteSpace(failureCode)
            && failureCode.Length <= CustomLoopLimits.MaxTraceReferenceCharacters
            && string.Equals(failureCode, failureCode.Trim(), StringComparison.Ordinal)
            && !failureCode.Contains('\r', StringComparison.Ordinal)
            && !failureCode.Contains('\n', StringComparison.Ordinal)
                ? failureCode
                : "pure_node_rejected";
    }

    private static string WritePureNodeRejection(string failureCode, string detail)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(failureCode));
        return $"{CanonicalPureNodeFailureCodePrefix}{encoded}\n{detail}";
    }

    private static string BoundPureNodeFailureDetail(string failureCode, string detail)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(failureCode));
        var maximum = CustomLoopLimits.MaxRunDetailCharacters - CanonicalPureNodeFailureCodePrefix.Length - encoded.Length - 1;
        var bounded = string.IsNullOrWhiteSpace(detail)
            ? "The deterministic pure node was rejected by its exact bounded contract."
            : detail;
        if (bounded.Length <= maximum)
        {
            return bounded;
        }

        bounded = bounded[..maximum];
        return bounded.Length > 0 && char.IsHighSurrogate(bounded[^1]) ? bounded[..^1] : bounded;
    }

    private static bool TryReadPureNodeRejection(string durableDetail, out string failureCode, out string detail)
    {
        failureCode = string.Empty;
        detail = string.Empty;
        if (!durableDetail.StartsWith(CanonicalPureNodeFailureCodePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var separator = durableDetail.IndexOf('\n', CanonicalPureNodeFailureCodePrefix.Length);
        if (separator < 0)
        {
            return false;
        }

        var encodedFailureCode = durableDetail[CanonicalPureNodeFailureCodePrefix.Length..separator];
        byte[] encodedBytes;
        try
        {
            encodedBytes = Convert.FromBase64String(encodedFailureCode);
            failureCode = _strictUtf8.GetString(encodedBytes);
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            return false;
        }

        detail = durableDetail[(separator + 1)..];
        return durableDetail.Length <= CustomLoopLimits.MaxRunDetailCharacters
            && string.Equals(encodedFailureCode, Convert.ToBase64String(encodedBytes), StringComparison.Ordinal)
            && string.Equals(failureCode, BoundPureNodeFailureCode(failureCode), StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(detail)
            && string.Equals(detail, BoundPureNodeFailureDetail(failureCode, detail), StringComparison.Ordinal);
    }

    private async Task<CustomLoopOrderedRunResult> DispatchAndAdvanceSequentialExitAsync(
        SequentialExecutionContext context,
        CustomLoopRunRecord run,
        GovernedLoopSequentialPlanNode node,
        int attempt,
        string attemptOperationId,
        string actor,
        string detail,
        CancellationToken cancellationToken)
    {
        var prepared = await DispatchSequentialNodeAsync(
            context,
            run,
            node,
            attempt,
            actor,
            token => PrepareOrExecuteSequentialExitAsync(context, node, attempt, attemptOperationId, run, actor, detail, token),
            cancellationToken);
        if (prepared.Terminal is not null)
        {
            return prepared.Terminal;
        }

        if (prepared.PendingCheckpoint is null || prepared.PendingTerminal is null)
        {
            return await TerminateAsync(prepared.Run!, actor, CustomLoopRunStatus.NeedsReview, "canonical_exit_advancement_missing", "Canonical Exit evidence resolved, but the ordered handler returned no terminal checkpoint advancement.");
        }

        var frontier = CompleteSequentialFrontier(prepared.Run!, context, node, attempt, attemptOperationId);
        if (frontier is null)
        {
            return await TerminateAsync(prepared.Run!, actor, CustomLoopRunStatus.NeedsReview, "canonical_frontier_advancement_failed", "Canonical Exit evidence resolved, but it could not advance the exact Running frontier.");
        }

        return await CommitPreparedSequentialAdvancementAsync(prepared, actor, detail, context, frontier.Frontier, frontier.SkipEvents);
    }

    private async Task<RunAdvance> PrepareOrExecuteSequentialExitAsync(
        SequentialExecutionContext context,
        GovernedLoopSequentialPlanNode node,
        int attempt,
        string attemptOperationId,
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
                new SequentialNodeExecutionContext(context.Anchor.AdapterBinding, context.Artifact, node, RequireRunningSequentialActivation(run, node, attempt, attemptOperationId), attempt, attemptOperationId, context.AllowedCapabilityIds, context.AuditRecorder));
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
            new SequentialNodeExecutionContext(
                context.Anchor.AdapterBinding,
                context.Artifact,
                node,
                RequireRunningSequentialActivation(run, node, attempt, attemptOperationId),
                attempt,
                attemptOperationId,
                context.AllowedCapabilityIds,
                context.AuditRecorder));
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
        CustomLoopRunRecord run,
        GovernedLoopSequentialPlanNode node,
        int attempt,
        string actor,
        Func<CancellationToken, Task<RunAdvance>> execute,
        CancellationToken cancellationToken)
    {
        var activation = RequireRunningSequentialActivation(
            run,
            node,
            attempt,
            run.Frontier!.Payload.Nodes.Single(candidate => candidate.Status == GovernedLoopNodeExecutionStatus.Running).AttemptOperationId!);
        var dispatchRequest = new GovernedLoopSequentialNodeDispatchRequest(
            GovernedLoopSequentialNodeDispatchRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            node,
            activation,
            attempt);
        RunAdvance? advance = null;
        var disposition = GovernedLoopSequentialNodeHandlerResultStatus.Unknown;
        var handler = new SingleSequentialNodeHandler(
            node.Descriptor,
            async token =>
            {
                advance = await execute(token);
                var durableRun = advance.Run ?? advance.Terminal?.Run;
                var evidenceEvent = durableRun is null ? null : FindSequentialNodeEvidence(durableRun, node, activation, attempt);
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

        var stoppedPureRun = advance?.Terminal?.Run ?? advance?.Run;
        if (handler.WasInvoked
            && GovernedLoopSequentialNodeDescriptors.IsPure(node.Descriptor)
            && advance?.Terminal is not null
            && stoppedPureRun is not null
            && FindSequentialNodeEvidence(stoppedPureRun, node, activation, attempt) is null)
        {
            return advance;
        }

        var current = advance?.Run ?? advance?.Terminal?.Run;
        if (current is null)
        {
            return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.InvalidState, null, "Canonical node dispatch was rejected before ordered runtime work began."));
        }

        if (!handler.WasInvoked && cancellationToken.IsCancellationRequested)
        {
            var cancelled = await CancelBeforeDispatchAsync(current, actor);
            return new RunAdvance(cancelled.Run, cancelled);
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
        var attemptStarted = Event(sequenceOwner, now, CustomLoopRunEventKind.NodeAttemptStarted, "Inference attempt trace committed before provider dispatch.", iteration, step.Id, attempt, assembly.Blocks, provider: run.ModelSnapshot.Provider, model: run.ModelSnapshot.Model, providerResponseId: correlation, toolAuthority: authority, traceReservationUtf8Bytes: CustomLoopLimits.MaxAttemptEvidenceReservationUtf8Bytes, eventId: sequentialNode?.AttemptOperationId);
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
        catch (GovernedLoopEffectAuthorityStoppedException exception)
        {
            var denied = IsExactDefinitiveProviderAuthorityDeny(exception, run, sequentialNode, correlation);
            return await RecordAttemptFailureAsync(
                run,
                actor,
                step.Id,
                iteration,
                correlation,
                assembly,
                exception,
                isExit: false,
                providerWasInvoked: providerInvoked,
                sequentialNode,
                denied ? CustomLoopRunStatus.Failed : CustomLoopRunStatus.NeedsReview,
                denied ? "effect_authority_denied" : "effect_authority_requires_review",
                denied
                    ? "The exact governed effect was durably denied without crossing its protected boundary."
                    : "The governed effect stopped without a definitive denial or completed outcome and requires review.");
        }
        catch (ToolActuationReviewRequiredException exception)
        {
            return await RecordAttemptFailureAsync(
                run,
                actor,
                step.Id,
                iteration,
                correlation,
                assembly,
                exception,
                isExit: false,
                providerWasInvoked: providerInvoked,
                sequentialNode,
                CustomLoopRunStatus.NeedsReview,
                "tool_actuation_requires_review",
                "Governed tool actuation stopped after approval because its authority outcome requires operator review.");
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
            : await PublishIfSelectedAsync(run, assembly.ResolvedOutputPolicy, retained, step.Id, isExit: false, actor, sequentialNode);
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
        catch (GovernedLoopEffectAuthorityStoppedException exception)
        {
            return await RecordAttemptFailureAsync(
                run,
                actor,
                "exit",
                iteration,
                correlation,
                assembly,
                exception,
                isExit: true,
                providerWasInvoked: providerInvoked,
                terminalStatusOverride: CustomLoopRunStatus.NeedsReview,
                failureCodeOverride: "effect_authority_requires_review",
                terminalDetailOverride: "The governed Exit effect stopped without a completed outcome and requires review.");
        }
        catch (ToolActuationReviewRequiredException exception)
        {
            return await RecordAttemptFailureAsync(
                run,
                actor,
                "exit",
                iteration,
                correlation,
                assembly,
                exception,
                isExit: true,
                providerWasInvoked: providerInvoked,
                terminalStatusOverride: CustomLoopRunStatus.NeedsReview,
                failureCodeOverride: "tool_actuation_requires_review",
                terminalDetailOverride: "Governed tool actuation stopped after approval because its authority outcome requires operator review.");
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

        return await CommitPreparedSequentialAdvancementAsync(prepared, actor, detail, null);
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
                traceReservationUtf8Bytes: CustomLoopLimits.MaxAttemptEvidenceReservationUtf8Bytes,
                eventId: sequentialNode.AttemptOperationId);
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
            : await PublishIfSelectedAsync(run, outputPolicy, iterationResult, "exit", isExit: true, actor, sequentialNode);
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
        string detail,
        SequentialExecutionContext? sequentialContext,
        GovernedLoopFrontierPosture? frontier = null,
        IReadOnlyList<CustomLoopRunEvent>? skipEvents = null)
    {
        var terminal = prepared.PendingTerminal!;
        if (terminal.Status == CustomLoopRunStatus.Completed && sequentialContext is not null)
        {
            var canonicalBoundary = await ObserveControlBoundaryAsync(prepared.Run!, actor);
            if (canonicalBoundary.Terminal is not null)
            {
                return canonicalBoundary.Terminal;
            }

            return await CompleteCanonicalAsync(
                canonicalBoundary.Run!,
                sequentialContext,
                terminal.Detail,
                terminal.FinalOutput ?? string.Empty,
                prepared.PendingCheckpoint,
                frontier,
                skipEvents);
        }

        var committed = await CommitCheckpointAsync(prepared.Run!, prepared.PendingCheckpoint!, detail, frontier, skipEvents);
        if (committed.Terminal is not null)
        {
            return committed.Terminal;
        }

        var completionBoundary = await ObserveControlBoundaryAsync(committed.Run!, actor);
        if (completionBoundary.Terminal is not null)
        {
            return completionBoundary.Terminal;
        }

        return await TerminateAsync(
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
        SequentialNodeExecutionContext? sequentialNode = null)
    {
        if (!policy.PublishToInvokingConversation)
        {
            return new RunAdvance(run, null);
        }

        if (sequentialNode is not null
            && (!isExit || !Equals(sequentialNode.Node.Descriptor, GovernedLoopSequentialNodeDescriptors.SuccessExit)))
        {
            var terminal = await TerminateAsync(
                run,
                actor,
                CustomLoopRunStatus.NeedsReview,
                "canonical_publication_node_invalid",
                "Canonical conversation publication is supported only for the exact success-Exit node; no publisher or append was invoked.");
            return new RunAdvance(terminal.Run, terminal);
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

        ConversationPublicationCommitBoundary? appendCommitBoundary = null;
        var canonicalPublicationAuthorityCompleted = 0;
        var canonicalPublicationAuthorityInvocations = 0;
        var canonicalPublicationAuthorityProtocolFailed = 0;
        if (sequentialNode is not null)
        {
            if (_conversationPublicationAuthorityBoundaryProvider is null)
            {
                var terminal = await TerminateAsync(
                    run,
                    actor,
                    CustomLoopRunStatus.NeedsReview,
                    "canonical_publication_authority_unavailable",
                    "No canonical conversation-publication authority boundary was composed; no publisher or append was invoked.");
                return new RunAdvance(terminal.Run, terminal);
            }

            try
            {
                var authorityCommitBoundary = _conversationPublicationAuthorityBoundaryProvider.CreateCommitBoundary(
                    new GovernedLoopConversationPublicationAuthorityRequest(
                        sequentialNode.Binding.AdmissionReceipt,
                        sequentialNode.Binding.ExecutionBinding,
                        sequentialNode.Artifact,
                        sequentialNode.Node.NodeId,
                        sequentialNode.Attempt,
                        operationId));
                if (authorityCommitBoundary is null)
                {
                    throw new InvalidOperationException("The canonical publication authority provider returned no commit boundary.");
                }

                var boundaryMarker = new object();
                appendCommitBoundary = async (commitAppend, token) =>
                {
                    if (Interlocked.Increment(ref canonicalPublicationAuthorityInvocations) != 1)
                    {
                        Volatile.Write(ref canonicalPublicationAuthorityProtocolFailed, 1);
                        throw new InvalidOperationException("The canonical publication-authority boundary may be invoked exactly once.");
                    }

                    var protocol = await ConversationPublicationCommitProtocol.ExecuteAsync(
                        authorityCommitBoundary,
                        async callbackToken =>
                        {
                            await commitAppend(callbackToken);
                            return boundaryMarker;
                        },
                        token);
                    if (protocol.Status == ConversationPublicationCommitProtocolStatus.Completed
                        && ReferenceEquals(protocol.Value, boundaryMarker))
                    {
                        Volatile.Write(ref canonicalPublicationAuthorityCompleted, 1);
                        return;
                    }

                    Volatile.Write(ref canonicalPublicationAuthorityProtocolFailed, 1);

                    if (protocol.Failure is not null)
                    {
                        ExceptionDispatchInfo.Capture(protocol.Failure).Throw();
                    }

                    throw new InvalidOperationException($"The canonical publication-authority boundary did not complete its exact append callback ({protocol.Status}).");
                };
            }
            catch (Exception exception)
            {
                var terminal = await TerminateAsync(
                    run,
                    actor,
                    CustomLoopRunStatus.NeedsReview,
                    "canonical_publication_authority_invalid",
                    $"The exact canonical conversation-publication boundary could not be created: {SafeExceptionClass(exception)}; no publisher or append was invoked.");
                return new RunAdvance(terminal.Run, terminal);
            }
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
                capabilityFailure = await GetCapabilityFailureAsync(run, publicationToken.Token, sequentialNode?.AllowedCapabilityIds);
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

            var request = new CustomLoopConversationPublicationRequest(
                operationId,
                run.Id,
                run.LoopId,
                run.Checkpoint.Iteration,
                stepId,
                conversation.ConversationId,
                conversation.CapturedVersion,
                output.Content,
                output.ContentHash,
                priorPublications,
                () => publicationDispatched = true,
                appendCommitBoundary);
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
        catch (GovernedLoopEffectAuthorityStoppedException exception) when (sequentialNode is not null && !publicationDispatched)
        {
            var definitiveDeny = IsExactDefinitivePublicationAuthorityDeny(exception, run, sequentialNode, operationId);
            var terminal = await TerminateAsync(
                run,
                actor,
                definitiveDeny ? CustomLoopRunStatus.Failed : CustomLoopRunStatus.NeedsReview,
                definitiveDeny ? "conversation_publication_authority_denied" : "conversation_publication_authority_stopped",
                definitiveDeny
                    ? "Exact durable authority denied the canonical conversation publication before append."
                    : "Canonical conversation-publication authority stopped before append and requires review.");
            return new RunAdvance(terminal.Run, terminal);
        }
        catch (Exception exception) when (sequentialNode is not null && !publicationDispatched)
        {
            var terminal = await TerminateAsync(
                run,
                actor,
                CustomLoopRunStatus.NeedsReview,
                "conversation_publication_boundary_failed",
                $"The conversation-publication boundary failed before append: {SafeExceptionClass(exception)}.");
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
        var reportsPublished = publication.Outcome is CustomLoopConversationPublicationOutcome.Published or CustomLoopConversationPublicationOutcome.AlreadyPublished;
        var canonicalAuthorityProven = sequentialNode is null
            || Volatile.Read(ref canonicalPublicationAuthorityCompleted) == 1
                && Volatile.Read(ref canonicalPublicationAuthorityInvocations) == 1
                && Volatile.Read(ref canonicalPublicationAuthorityProtocolFailed) == 0
                && publicationDispatched;
        var isPublished = publicationIdMatches && reportsPublished && canonicalAuthorityProven;
        var publicationId = operationId;
        var eventDetail = !publicationIdMatches
            ? PublicationMismatchedIdentityDetail
            : reportsPublished && !canonicalAuthorityProven
                ? PublicationAuthorityUnprovenDetail
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

    private async Task<RunAdvance> CommitCheckpointAsync(
        CustomLoopRunRecord run,
        CustomLoopRunCheckpoint checkpoint,
        string detail,
        GovernedLoopFrontierPosture? frontier = null,
        IReadOnlyList<CustomLoopRunEvent>? precedingEvents = null)
    {
        var now = Now(run);
        var events = precedingEvents ?? [];
        var checkpointOwner = run with { Events = [.. run.Events, .. events] };
        var checkpointEvent = Event(checkpointOwner, now, CustomLoopRunEventKind.CheckpointCommitted, detail, checkpoint.Iteration);
        var committedCheckpoint = checkpoint with { LastCommittedSequence = checkpointEvent.Sequence };
        var candidate = Append(run, now, [.. events, checkpointEvent]) with
        {
            Checkpoint = committedCheckpoint,
            ExecutionClock = AdvanceClock(run.ExecutionClock, now, terminal: false),
            Frontier = frontier ?? run.Frontier,
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

        var needsReviewOverride = terminalStatusOverride == CustomLoopRunStatus.NeedsReview;
        var uncertain = needsReviewOverride || providerWasInvoked && IsUncertainProviderFailure(exception);
        var needsReview = needsReviewOverride || providerWasInvoked && (isExit || uncertain);
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

    private static bool IsExactDefinitiveProviderAuthorityDeny(
        GovernedLoopEffectAuthorityStoppedException exception,
        CustomLoopRunRecord run,
        SequentialNodeExecutionContext? sequentialNode,
        string correlationId)
    {
        if (sequentialNode is null
            || exception.ExecutionStatus != GovernedLoopEffectAuthorityExecutionStatus.Decided
            || exception.EvidenceStatus is not (GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended
                or GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent)
            || exception.Decision is not { Disposition: GovernedLoopEffectAuthorityDisposition.Deny } decision
            || !Equals(sequentialNode.Node.Descriptor, GovernedLoopSequentialNodeDescriptors.ProviderInference)
            || !GovernedLoopEffectAuthorityContractValidator.Validate(decision).IsValid)
        {
            return false;
        }

        try
        {
            var receipt = sequentialNode.Binding.AdmissionReceipt;
            var graphNode = sequentialNode.Artifact.Graph.Nodes.SingleOrDefault(
                item => string.Equals(item.Id, sequentialNode.Node.NodeId, StringComparison.Ordinal));
            if (graphNode is null || !Equals(graphNode.Descriptor, GovernedLoopSequentialNodeDescriptors.ProviderInference))
            {
                return false;
            }

            var requiresWorkspace = graphNode.AuthorityCeiling.CapabilityIds.Contains(
                SequentialWorkspaceCommandCapabilityId,
                StringComparer.Ordinal);
            var requiredCapabilityIds = requiresWorkspace
                ? new[] { SequentialModelInferenceCapabilityId, SequentialWorkspaceCommandCapabilityId }
                : [SequentialModelInferenceCapabilityId];
            var requiredPins = requiredCapabilityIds.Select(capabilityId => receipt.Evidence.CapabilityAdmission.Pins.SingleOrDefault(
                pin => string.Equals(pin.DescriptorIdentity.Id.Value, capabilityId, StringComparison.Ordinal))).ToArray();
            if (requiredPins.Any(pin => pin is null))
            {
                return false;
            }

            var pins = requiredPins.Select(pin => pin!).ToArray();
            var requiredAuthority = new AuthorityCeiling(
                pins.Select(pin => pin.DescriptorIdentity).ToArray(),
                receipt.Evidence.EffectiveAuthority.DataClasses,
                requiresWorkspace ? 1 : 0,
                requiresWorkspace ? CapabilitySideEffectClass.ReadOnly : CapabilitySideEffectClass.None,
                false,
                false,
                false);
            var effectOperationId = "provider-" + CustomLoopTraceContentHash.Compute(
                $"provider-transport-v1\n{run.Id}\n{sequentialNode.Node.NodeId}\n{sequentialNode.Attempt}\n{correlationId}");
            var expectedRequest = new GovernedLoopEffectAuthorityRequest(
                receipt,
                sequentialNode.Binding.ExecutionBinding,
                sequentialNode.Artifact,
                sequentialNode.Node.NodeId,
                sequentialNode.Attempt,
                effectOperationId,
                correlationId,
                GovernedLoopEffectBoundaryKind.ProviderTransport,
                requiredAuthority,
                pins);
            return string.Equals(run.Id, expectedRequest.ExecutionBinding.RunId, StringComparison.Ordinal)
                && GovernedLoopEffectAuthorityDecisionMatcher.IsExactMatch(decision, expectedRequest);
        }
        catch (Exception malformed) when (malformed is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsExactDefinitivePublicationAuthorityDeny(
        GovernedLoopEffectAuthorityStoppedException exception,
        CustomLoopRunRecord run,
        SequentialNodeExecutionContext? sequentialNode,
        string publicationOperationId)
    {
        if (sequentialNode is null
            || exception.ExecutionStatus != GovernedLoopEffectAuthorityExecutionStatus.Decided
            || exception.EvidenceStatus is not (GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended
                or GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent)
            || exception.Decision is not { Disposition: GovernedLoopEffectAuthorityDisposition.Deny } decision
            || !Equals(sequentialNode.Node.Descriptor, GovernedLoopSequentialNodeDescriptors.SuccessExit)
            || !GovernedLoopEffectAuthorityContractValidator.Validate(decision).IsValid)
        {
            return false;
        }

        try
        {
            var receipt = sequentialNode.Binding.AdmissionReceipt;
            var conversationPins = receipt.Evidence.CapabilityAdmission.Pins
                .Where(item => string.Equals(item.DescriptorIdentity.Id.Value, SequentialConversationTurnCapabilityId, StringComparison.Ordinal))
                .ToArray();
            var graphNode = sequentialNode.Artifact.Graph.Nodes.SingleOrDefault(
                item => string.Equals(item.Id, sequentialNode.Node.NodeId, StringComparison.Ordinal));
            if (conversationPins.Length != 1
                || graphNode is null
                || !Equals(graphNode.Descriptor, GovernedLoopSequentialNodeDescriptors.SuccessExit)
                || !graphNode.AuthorityCeiling.CapabilityIds.Contains(SequentialConversationTurnCapabilityId, StringComparer.Ordinal))
            {
                return false;
            }

            var requiredAuthority = new AuthorityCeiling(
                [conversationPins[0].DescriptorIdentity],
                receipt.Evidence.EffectiveAuthority.DataClasses,
                1,
                CapabilitySideEffectClass.None,
                false,
                true,
                false);
            var targetFingerprint = GovernedLoopEffectAuthorityOperationIdentity.CreateConversationPublicationTargetFingerprint(receipt);
            var effectOperationId = GovernedLoopEffectAuthorityOperationIdentity.CreateConversationPublication(
                receipt,
                sequentialNode.Binding.ExecutionBinding,
                sequentialNode.Artifact,
                sequentialNode.Node.NodeId,
                sequentialNode.Attempt,
                publicationOperationId,
                targetFingerprint);
            var expectedRequest = new GovernedLoopEffectAuthorityRequest(
                receipt,
                sequentialNode.Binding.ExecutionBinding,
                sequentialNode.Artifact,
                sequentialNode.Node.NodeId,
                sequentialNode.Attempt,
                effectOperationId,
                publicationOperationId,
                GovernedLoopEffectBoundaryKind.ConversationPublication,
                requiredAuthority,
                conversationPins,
                targetFingerprint);
            return string.Equals(run.Id, expectedRequest.ExecutionBinding.RunId, StringComparison.Ordinal)
                && GovernedLoopEffectAuthorityDecisionMatcher.IsExactMatch(decision, expectedRequest);
        }
        catch (Exception malformed) when (malformed is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
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
            traceReservationUtf8Bytes: CustomLoopLimits.MaxAttemptEvidenceReservationUtf8Bytes,
            eventId: sequentialNode.AttemptOperationId);
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
            || string.Equals(rejection.Detail, CanonicalDurableCancellationDetail, StringComparison.Ordinal)
            || string.Equals(rejection.Detail, CanonicalPureNodeCancellationDetail, StringComparison.Ordinal);

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
        var pausedFrontier = ProjectPausedFrontier(run, lifecycle);
        if (run.SequentialAdapterBinding is not null && pausedFrontier is null)
        {
            var terminal = await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, "canonical_pause_frontier_failed", "The proved pause boundary could not be composed with the exact durable frontier.");
            return new RunAdvance(terminal.Run, terminal);
        }

        var candidate = run with
        {
            LifecycleVersion = run.LifecycleVersion + 1,
            Status = CustomLoopRunStatus.Paused,
            UpdatedAtUtc = now,
            ExecutionClock = AdvanceClock(run.ExecutionClock, now, terminal: true),
            Events = [.. run.Events, lifecycle],
            Frontier = pausedFrontier,
        };
        var persisted = await PersistAsync(run, candidate, IntegrityToken(), outcomeMayExist: false);
        if (persisted.Terminal is not null)
        {
            return persisted;
        }

        return new RunAdvance(persisted.Run, Result(CustomLoopOrderedRunStatus.Paused, persisted.Run, "The run is Paused at a committed checkpoint; no later attempt was dispatched."));
    }

    private static GovernedLoopFrontierPosture? ProjectPausedFrontier(CustomLoopRunRecord run, CustomLoopRunEvent lifecycle)
    {
        if (run.SequentialAdapterBinding is not { } binding || run.Frontier is not { } frontier)
        {
            return run.Frontier;
        }

        var running = FindSoleFrontierActivation(frontier, GovernedLoopNodeExecutionStatus.Running);
        if (running is null)
        {
            return frontier;
        }

        var exactOutcome = FindExactClosedSequentialOutcome(run, running);
        var exactEvidence = exactOutcome?.SequentialNodeEvidence;
        var blocked = GovernedLoopSequentialFrontierMachine.ReviewBlockCurrent(
            frontier,
            binding,
            exactOutcome?.EventId,
            exactEvidence?.OutcomeArtifactHash,
            exactEvidence?.ControlOutcome,
            exactEvidence?.SelectedControlEdgeIds,
            exactEvidence?.SkippedControlEdgeIds,
            lifecycle.TimestampUtc);
        return blocked.Status == GovernedLoopSequentialFrontierTransitionStatus.Applied ? blocked.Frontier : null;
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

    private async Task<RunAdvance> RefreshPureControlUpdateAsync(CustomLoopRunRecord run)
    {
        CustomLoopRunRecord? latest;
        try
        {
            latest = await _runStore.GetAsync(run.Id, IntegrityToken());
        }
        catch (Exception exception)
        {
            return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.NeedsReview, run, $"The durable run could not be refreshed at the deterministic pure-node boundary: {SafeExceptionClass(exception)}."));
        }

        if (latest is null)
        {
            return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.NotFound, null, "The run trace disappeared at the deterministic pure-node boundary."));
        }

        if (latest.LifecycleVersion == run.LifecycleVersion)
        {
            return DurableTraceVersionMatches(run, latest)
                ? new RunAdvance(run, null)
                : new RunAdvance(null, Result(CustomLoopOrderedRunStatus.Conflict, latest, "The pure-node run changed without advancing its lifecycle version; no evaluation or replay was attempted."));
        }

        if (!IsAcceptedPureControlSuccessor(run, latest))
        {
            return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.Conflict, latest, "The pure-node run changed outside the exact lifecycle-only pause/cancel protocol; no evaluation or replay was attempted."));
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

    private static bool IsAcceptedPureControlSuccessor(CustomLoopRunRecord current, CustomLoopRunRecord latest)
        => (current.Status, latest.Status) switch
        {
            (CustomLoopRunStatus.Running, CustomLoopRunStatus.PauseRequested)
                => IsAcceptedPureLifecycleChain(current, latest, CustomLoopRunStatus.PauseRequested),
            (CustomLoopRunStatus.Running, CustomLoopRunStatus.CancelRequested)
                => IsAcceptedPureLifecycleChain(current, latest, CustomLoopRunStatus.CancelRequested)
                    || IsAcceptedPureLifecycleChain(current, latest, CustomLoopRunStatus.PauseRequested, CustomLoopRunStatus.CancelRequested),
            (CustomLoopRunStatus.PauseRequested, CustomLoopRunStatus.CancelRequested)
                => IsAcceptedPureLifecycleChain(current, latest, CustomLoopRunStatus.CancelRequested),
            _ => false,
        };

    private static bool TryGetAcceptedPureCancellationTerminalSuccessor(
        CustomLoopRunRecord current,
        CustomLoopRunRecord latest,
        CustomLoopRunEvent durableFailure,
        out CustomLoopRunEvent? terminalLifecycle)
    {
        terminalLifecycle = null;
        if (latest.Status != CustomLoopRunStatus.Cancelled
            || !latest.IsTerminal
            || !HasExactRetainedPureRejection(latest, durableFailure)
            || !CustomLoopRunValidator.Validate(latest).IsValid)
        {
            return false;
        }

        return TryGetAcceptedPureCancellationLifecycleSuccessor(current, latest, out terminalLifecycle);
    }

    private static bool TryGetAcceptedPureCancellationLifecycleSuccessor(
        CustomLoopRunRecord current,
        CustomLoopRunRecord latest,
        out CustomLoopRunEvent? terminalLifecycle)
    {
        terminalLifecycle = null;
        if (latest.Status != CustomLoopRunStatus.Cancelled
            || !latest.IsTerminal
            || !CustomLoopRunValidator.HasExactDurableEventPrefix(current, latest))
        {
            return false;
        }

        var terminalSnapshot = WithoutTerminalIntegrityWarning(latest);
        if (terminalSnapshot is null)
        {
            return false;
        }

        var accepted = current.Status switch
        {
            CustomLoopRunStatus.CancelRequested
                => IsAcceptedPureLifecycleChain(current, terminalSnapshot, CustomLoopRunStatus.Cancelled),
            CustomLoopRunStatus.PauseRequested
                => IsAcceptedPureLifecycleChain(current, terminalSnapshot, CustomLoopRunStatus.CancelRequested, CustomLoopRunStatus.Cancelled),
            CustomLoopRunStatus.Running
                => IsAcceptedPureLifecycleChain(current, terminalSnapshot, CustomLoopRunStatus.CancelRequested, CustomLoopRunStatus.Cancelled)
                    || IsAcceptedPureLifecycleChain(
                        current,
                        terminalSnapshot,
                        CustomLoopRunStatus.PauseRequested,
                        CustomLoopRunStatus.CancelRequested,
                        CustomLoopRunStatus.Cancelled),
            _ => false,
        };
        if (!accepted)
        {
            return false;
        }

        terminalLifecycle = GetCanonicalTerminalLifecycleEvent(terminalSnapshot);
        return terminalLifecycle is not null
            && terminalLifecycle.Sequence == terminalSnapshot.Events.Length
            && terminalSnapshot.CompletedAtUtc == terminalLifecycle.TimestampUtc
            && string.Equals(terminalLifecycle.Detail, CanonicalDurableCancellationDetail, StringComparison.Ordinal)
            && terminalLifecycle.ControlExpectedLifecycleVersion is null;
    }

    private static bool TryGetAcceptedPurePausedLifecycleSuccessor(
        CustomLoopRunRecord current,
        CustomLoopRunRecord latest)
    {
        if (latest.Status != CustomLoopRunStatus.Paused
            || latest.IsTerminal
            || !CustomLoopRunValidator.HasExactDurableEventPrefix(current, latest))
        {
            return false;
        }

        var appended = latest.Events.Skip(current.Events.Length).ToArray();
        var pausePredecessor = current;
        if (appended.Length == 2 && current.Status == CustomLoopRunStatus.Running)
        {
            if (current.LifecycleVersion == int.MaxValue)
            {
                return false;
            }

            var pauseRequested = current with
            {
                LifecycleVersion = current.LifecycleVersion + 1,
                Status = CustomLoopRunStatus.PauseRequested,
                UpdatedAtUtc = appended[0].TimestampUtc,
                Events = [.. current.Events, appended[0]],
            };
            if (!IsAcceptedPureLifecycleChain(current, pauseRequested, CustomLoopRunStatus.PauseRequested))
            {
                return false;
            }

            pausePredecessor = pauseRequested;
            appended = appended[1..];
        }

        const string PausedDetail = "The run entered Paused at a proved checkpoint boundary; Resume is required for any later dispatch.";
        if (pausePredecessor.Status != CustomLoopRunStatus.PauseRequested
            || appended is not [{ Kind: CustomLoopRunEventKind.LifecycleChanged } lifecycle]
            || lifecycle.Sequence != pausePredecessor.Events.Length + 1L
            || !string.Equals(lifecycle.Detail, PausedDetail, StringComparison.Ordinal)
            || lifecycle.ControlExpectedLifecycleVersion is not null
            || pausePredecessor.LifecycleVersion == int.MaxValue)
        {
            return false;
        }

        var expectedFrontier = ProjectPausedFrontier(pausePredecessor, lifecycle);
        if (pausePredecessor.SequentialAdapterBinding is not null && expectedFrontier is null)
        {
            return false;
        }

        var expectedPaused = pausePredecessor with
        {
            LifecycleVersion = pausePredecessor.LifecycleVersion + 1,
            Status = CustomLoopRunStatus.Paused,
            UpdatedAtUtc = lifecycle.TimestampUtc,
            ExecutionClock = AdvanceClock(pausePredecessor.ExecutionClock, lifecycle.TimestampUtc, terminal: true),
            Events = [.. pausePredecessor.Events, lifecycle],
            Frontier = expectedFrontier,
        };
        return CustomLoopRunValidator.ValidateUpdate(pausePredecessor, expectedPaused).IsValid
            && CustomLoopRunValidator.HasSameDurableVersion(expectedPaused, latest);
    }

    private static bool TryGetAcceptedPureRejectionTerminalSuccessor(
        CustomLoopRunRecord rejectionSnapshot,
        CustomLoopRunRecord latest,
        CustomLoopRunStatus requestedStatus,
        string? failureCode,
        string detail,
        CustomLoopRunEvent durableFailure,
        out CustomLoopRunStatus terminalStatus,
        out CustomLoopRunEvent? terminalLifecycle)
    {
        terminalStatus = latest.Status;
        terminalLifecycle = null;
        if (!latest.IsTerminal
            || !HasExactRetainedPureRejection(latest, durableFailure)
            || !CustomLoopRunValidator.HasExactDurableEventPrefix(rejectionSnapshot, latest))
        {
            return false;
        }

        if (latest.Status == CustomLoopRunStatus.Cancelled)
        {
            terminalStatus = CustomLoopRunStatus.Cancelled;
            return TryGetAcceptedPureCancellationTerminalSuccessor(
                rejectionSnapshot,
                latest,
                durableFailure,
                out terminalLifecycle);
        }

        if (requestedStatus is not (CustomLoopRunStatus.Failed or CustomLoopRunStatus.NeedsReview)
            || latest.Status != requestedStatus)
        {
            return false;
        }

        var terminalSnapshot = WithoutTerminalIntegrityWarning(latest);
        if (terminalSnapshot is null
            || !CustomLoopRunValidator.HasExactDurableEventPrefix(rejectionSnapshot, terminalSnapshot)
            || rejectionSnapshot.LifecycleVersion == int.MaxValue)
        {
            return false;
        }

        var appended = terminalSnapshot.Events.Skip(rejectionSnapshot.Events.Length).ToArray();
        var terminalPredecessor = rejectionSnapshot;
        if (appended.Length == 2
            && rejectionSnapshot.Status == CustomLoopRunStatus.Running)
        {
            var paused = rejectionSnapshot with
            {
                LifecycleVersion = checked(rejectionSnapshot.LifecycleVersion + 1),
                Status = CustomLoopRunStatus.PauseRequested,
                UpdatedAtUtc = appended[0].TimestampUtc,
                Events = [.. rejectionSnapshot.Events, appended[0]],
            };
            if (!IsAcceptedPureLifecycleChain(rejectionSnapshot, paused, CustomLoopRunStatus.PauseRequested))
            {
                return false;
            }

            terminalPredecessor = paused;
            appended = appended[1..];
        }

        if (appended is not [{ Kind: CustomLoopRunEventKind.LifecycleChanged } lifecycle]
            || lifecycle.Sequence != terminalPredecessor.Events.Length + 1L
            || !string.Equals(lifecycle.Detail, detail, StringComparison.Ordinal)
            || lifecycle.ControlExpectedLifecycleVersion is not null
            || terminalPredecessor.LifecycleVersion == int.MaxValue)
        {
            return false;
        }

        var expectedFrontier = ProjectTerminalFrontier(terminalPredecessor, requestedStatus, lifecycle);
        if (terminalPredecessor.SequentialAdapterBinding is not null && expectedFrontier is null)
        {
            return false;
        }

        var expectedTerminal = terminalPredecessor with
        {
            LifecycleVersion = terminalPredecessor.LifecycleVersion + 1,
            Status = requestedStatus,
            UpdatedAtUtc = lifecycle.TimestampUtc,
            CompletedAtUtc = lifecycle.TimestampUtc,
            ExecutionClock = AdvanceClock(terminalPredecessor.ExecutionClock, lifecycle.TimestampUtc, terminal: true),
            Events = [.. terminalPredecessor.Events, lifecycle],
            FinalOutput = null,
            FailureCode = failureCode,
            FailureDetail = detail,
            Frontier = expectedFrontier,
        };
        if (!CustomLoopRunValidator.ValidateUpdate(terminalPredecessor, expectedTerminal).IsValid
            || !CustomLoopRunValidator.HasSameDurableVersion(expectedTerminal, terminalSnapshot))
        {
            return false;
        }

        terminalStatus = requestedStatus;
        terminalLifecycle = lifecycle;
        return true;
    }

    private static CustomLoopRunRecord? WithoutTerminalIntegrityWarning(CustomLoopRunRecord latest)
    {
        if (latest.Events.LastOrDefault() is not { Kind: CustomLoopRunEventKind.IntegrityWarning } warning)
        {
            return latest;
        }

        if (latest.LifecycleVersion <= 1 || latest.Events.Length <= 1)
        {
            return null;
        }

        var terminalEvents = latest.Events[..^1];
        var terminalLifecycle = terminalEvents.LastOrDefault();
        if (terminalLifecycle?.Kind != CustomLoopRunEventKind.LifecycleChanged)
        {
            return null;
        }

        var terminalSnapshot = latest with
        {
            LifecycleVersion = latest.LifecycleVersion - 1,
            UpdatedAtUtc = terminalLifecycle.TimestampUtc,
            Events = terminalEvents,
        };
        var expectedWarned = terminalSnapshot with
        {
            LifecycleVersion = latest.LifecycleVersion,
            UpdatedAtUtc = warning.TimestampUtc,
            Events = [.. terminalSnapshot.Events, warning],
        };
        return CustomLoopRunValidator.ValidateTerminalIntegrityWarningAppend(terminalSnapshot, warning).IsValid
            && CustomLoopRunValidator.HasSameDurableVersion(expectedWarned, latest)
                ? terminalSnapshot
                : null;
    }

    private static bool IsAcceptedPureLifecycleChain(
        CustomLoopRunRecord current,
        CustomLoopRunRecord latest,
        params CustomLoopRunStatus[] successorStatuses)
    {
        var appended = latest.Events.Skip(current.Events.Length).ToArray();
        if (successorStatuses.Length == 0
            || current.LifecycleVersion > int.MaxValue - successorStatuses.Length
            || appended.Length != successorStatuses.Length
            || latest.LifecycleVersion != current.LifecycleVersion + successorStatuses.Length
            || latest.Status != successorStatuses[^1]
            || appended.Any(item => item.Kind != CustomLoopRunEventKind.LifecycleChanged))
        {
            return false;
        }

        var predecessor = current;
        for (var index = 0; index < successorStatuses.Length; index++)
        {
            var candidate = index == successorStatuses.Length - 1
                ? latest
                : predecessor with
                {
                    LifecycleVersion = checked(predecessor.LifecycleVersion + 1),
                    Status = successorStatuses[index],
                    UpdatedAtUtc = appended[index].TimestampUtc,
                    Events = [.. predecessor.Events, appended[index]],
                };
            if (candidate.Status != successorStatuses[index]
                || !CustomLoopRunValidator.ValidateUpdate(predecessor, candidate).IsValid
                || !IsExactPureLifecycleLeg(predecessor, candidate, appended[index], successorStatuses[index]))
            {
                return false;
            }

            predecessor = candidate;
        }

        return true;
    }

    private static bool IsExactPureLifecycleLeg(
        CustomLoopRunRecord predecessor,
        CustomLoopRunRecord candidate,
        CustomLoopRunEvent lifecycle,
        CustomLoopRunStatus successorStatus)
    {
        if (candidate.UpdatedAtUtc != lifecycle.TimestampUtc
            || !CheckpointsEqual(predecessor.Checkpoint, candidate.Checkpoint)
            || candidate.CompletedAtUtc != (successorStatus == CustomLoopRunStatus.Cancelled ? lifecycle.TimestampUtc : predecessor.CompletedAtUtc)
            || !string.Equals(predecessor.FinalOutput, candidate.FinalOutput, StringComparison.Ordinal)
            || !string.Equals(predecessor.FailureCode, candidate.FailureCode, StringComparison.Ordinal)
            || !string.Equals(predecessor.FailureDetail, candidate.FailureDetail, StringComparison.Ordinal))
        {
            return false;
        }

        if (successorStatus != CustomLoopRunStatus.Cancelled)
        {
            return Equals(predecessor.ExecutionClock, candidate.ExecutionClock)
                && string.Equals(predecessor.Frontier?.Payload.ContentHash, candidate.Frontier?.Payload.ContentHash, StringComparison.Ordinal);
        }

        var expectedFrontier = ProjectTerminalFrontier(predecessor, CustomLoopRunStatus.Cancelled, lifecycle);
        var expectedClock = AdvanceClock(predecessor.ExecutionClock, lifecycle.TimestampUtc, terminal: true);
        return expectedFrontier is not null
            && Equals(expectedClock, candidate.ExecutionClock)
            && string.Equals(expectedFrontier.Payload.ContentHash, candidate.Frontier?.Payload.ContentHash, StringComparison.Ordinal);
    }

    private static bool HasExactRetainedPureRejection(CustomLoopRunRecord run, CustomLoopRunEvent durableFailure)
    {
        var matches = run.Events.Where(item => string.Equals(item.EventId, durableFailure.EventId, StringComparison.Ordinal)).ToArray();
        return matches.Length == 1
            && matches[0].SequentialNodeEvidence is
            {
                Kind: CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection,
                Disposition: CustomLoopSequentialNodeDisposition.Rejected,
            } evidence
            && durableFailure.SequentialNodeEvidence is
            {
                Kind: CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection,
                Disposition: CustomLoopSequentialNodeDisposition.Rejected,
            } expectedEvidence
            && string.Equals(evidence.EvidenceHash, expectedEvidence.EvidenceHash, StringComparison.Ordinal)
            && string.Equals(evidence.OutcomeArtifactHash, expectedEvidence.OutcomeArtifactHash, StringComparison.Ordinal)
            && CustomLoopSequentialOutcomeArtifactHash.Matches(matches[0]);
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

    private async Task<CustomLoopOrderedRunResult> CompleteCanonicalAsync(
        CustomLoopRunRecord run,
        SequentialExecutionContext context,
        string detail,
        string finalOutput,
        CustomLoopRunCheckpoint? pendingCheckpoint = null,
        GovernedLoopFrontierPosture? completedFrontier = null,
        IReadOnlyList<CustomLoopRunEvent>? precedingEvents = null)
    {
        if ((pendingCheckpoint is null) != (completedFrontier is null)
            || completedFrontier is not null && completedFrontier.Payload.Status != GovernedLoopFrontierStatus.Completed)
        {
            return Result(CustomLoopOrderedRunStatus.InvalidState, run, "Canonical completion requires one atomic checkpoint and Completed-frontier successor pair.");
        }

        if (_firstBoundRunCompletionBoundary is null)
        {
            if (run.Status == CustomLoopRunStatus.Completed)
            {
                return Result(
                    CustomLoopOrderedRunStatus.NeedsReview,
                    run,
                    "The exact canonical run is durably Completed, but no completion boundary is composed to reconcile its grant ledger.");
            }

            return await TerminateAsync(
                run,
                run.AdmissionActor,
                CustomLoopRunStatus.NeedsReview,
                "canonical_completion_authority_unavailable",
                "No canonical first-bound-run completion boundary was composed; successful terminal persistence was not attempted.");
        }

        CustomLoopRunRecord? committedRun = null;
        var now = Now(run);
        var events = precedingEvents ?? [];
        CustomLoopRunEvent? checkpointEvent = null;
        CustomLoopRunCheckpoint? committedCheckpoint = null;
        CustomLoopRunEvent? terminalEvent = null;
        if (run.Status != CustomLoopRunStatus.Completed)
        {
            if (pendingCheckpoint is null || completedFrontier is null)
            {
                return Result(CustomLoopOrderedRunStatus.InvalidState, run, "Canonical successful completion cannot persist outside the atomic Exit checkpoint/frontier boundary.");
            }

            var checkpointOwner = run with { Events = [.. run.Events, .. events] };
            checkpointEvent = Event(checkpointOwner, now, CustomLoopRunEventKind.CheckpointCommitted, "Exit checkpoint and Completed frontier committed with terminal lifecycle.", pendingCheckpoint.Iteration);
            committedCheckpoint = pendingCheckpoint with { LastCommittedSequence = checkpointEvent.Sequence };
            var terminalOwner = run with { Events = [.. run.Events, .. events, checkpointEvent] };
            terminalEvent = Event(terminalOwner, now, CustomLoopRunEventKind.LifecycleChanged, detail);
        }

        var candidate = terminalEvent is null
            ? null
            : run with
            {
                LifecycleVersion = run.LifecycleVersion + 1,
                Status = CustomLoopRunStatus.Completed,
                UpdatedAtUtc = now,
                CompletedAtUtc = now,
                ExecutionClock = AdvanceClock(run.ExecutionClock, now, terminal: true),
                Checkpoint = committedCheckpoint!,
                Events = [.. run.Events, .. events, checkpointEvent!, terminalEvent],
                FinalOutput = finalOutput,
                FailureCode = null,
                FailureDetail = null,
                Frontier = completedFrontier,
            };

        GovernedLoopFirstBoundRunCompletionExecutionResult completion;
        try
        {
            completion = await _firstBoundRunCompletionBoundary.ExecuteAsync(
                context.Anchor.AdapterBinding.AdmissionReceipt,
                context.Anchor.AdapterBinding.ExecutionBinding,
                async token =>
                {
                    if (candidate is null)
                    {
                        committedRun = await ReloadExactCanonicalCompletedAsync(context);
                        if (committedRun is null)
                        {
                            throw new InvalidOperationException("The claimed canonical completion did not reload as the exact durable completed run.");
                        }

                        return;
                    }

                    try
                    {
                        var stored = await _runStore.UpdateAsync(candidate, run.LifecycleVersion, token);
                        if (stored.Status == CustomLoopRunStoreStatus.Updated
                            && stored.Run is not null
                            && IsExactCanonicalCompletedRun(stored.Run, context))
                        {
                            committedRun = stored.Run;
                            return;
                        }
                    }
                    catch
                    {
                        committedRun = await ReloadExactCanonicalCompletedAsync(context);
                        if (committedRun is not null)
                        {
                            return;
                        }

                        throw;
                    }

                    committedRun = await ReloadExactCanonicalCompletedAsync(context);
                    if (committedRun is null)
                    {
                        throw new InvalidOperationException("Successful canonical terminal persistence could not be authenticated after the store rejected its exact successor.");
                    }
                },
                IntegrityToken());
        }
        catch (Exception exception)
        {
            committedRun = await ReloadExactCanonicalCompletedAsync(context);
            if (committedRun is null)
            {
                return Result(
                    CustomLoopOrderedRunStatus.NeedsReview,
                    run,
                    $"Canonical grant-completion coordination stopped before exact successful terminal durability could be proved: {SafeExceptionClass(exception)}.");
            }

            return await CompleteCanonicalTerminalEvidenceAsync(
                committedRun,
                context,
                detail,
                $"durable grant-completion coordination returned {SafeExceptionClass(exception)} after the truthful terminal trace committed");
        }

        if (completion.Disposition == GovernedLoopFirstBoundRunCompletionDisposition.Rejected)
        {
            var definitive = completion.Status == GovernedLoopEffectAuthorityUsageStoreStatus.GrantCompleted;
            if (run.Status == CustomLoopRunStatus.Completed)
            {
                return Result(
                    CustomLoopOrderedRunStatus.NeedsReview,
                    run,
                    definitive
                        ? "The exact run is durably Completed, but the grant ledger reports another bound run completed first; operator reconciliation is required."
                        : $"The exact run is durably Completed, but canonical completion reconciliation was rejected with `{completion.Status}` and requires review.");
            }

            return await TerminateAsync(
                run,
                run.AdmissionActor,
                definitive ? CustomLoopRunStatus.Failed : CustomLoopRunStatus.NeedsReview,
                definitive ? "canonical_completion_authority_denied" : "canonical_completion_authority_unavailable",
                definitive
                    ? "The exact grant already completed another bound run; this run cannot commit successful terminal state."
                    : $"Canonical successful completion was rejected with `{completion.Status}` before terminal persistence and requires review.");
        }

        if (completion.Disposition is not (GovernedLoopFirstBoundRunCompletionDisposition.Completed
            or GovernedLoopFirstBoundRunCompletionDisposition.AlreadyCompleted
            or GovernedLoopFirstBoundRunCompletionDisposition.NeedsReview))
        {
            return Result(CustomLoopOrderedRunStatus.NeedsReview, run, "Canonical grant-completion coordination returned an unsupported disposition; successful terminal state is unproved.");
        }

        committedRun = await ReloadExactCanonicalCompletedAsync(context);
        if (committedRun is null)
        {
            return Result(CustomLoopOrderedRunStatus.NeedsReview, run, "The completion ledger advanced, but a fresh load could not authenticate the exact canonical Completed run.");
        }

        var completionIntegrityDetail = completion.Disposition == GovernedLoopFirstBoundRunCompletionDisposition.NeedsReview
            ? $"durable grant-completion evidence remained `{completion.Status}` after the truthful terminal trace committed"
            : null;
        return await CompleteCanonicalTerminalEvidenceAsync(committedRun, context, detail, completionIntegrityDetail);
    }

    private async Task<CustomLoopOrderedRunResult> CompleteCanonicalTerminalEvidenceAsync(
        CustomLoopRunRecord terminalRun,
        SequentialExecutionContext context,
        string successDetail,
        string? completionIntegrityDetail)
    {
        if (terminalRun.Events[^1].Kind == CustomLoopRunEventKind.IntegrityWarning)
        {
            return Result(CustomLoopOrderedRunStatus.NeedsReview, terminalRun, terminalRun.Events[^1].Detail);
        }

        var integrityFailures = new List<string>();
        if (completionIntegrityDetail is not null)
        {
            integrityFailures.Add(completionIntegrityDetail);
        }

        var terminalEvent = GetCanonicalTerminalLifecycleEvent(terminalRun)!;
        var terminalArtifactHash = CustomLoopSequentialOutcomeArtifactHash.Compute(terminalEvent);
        var terminalMetadata = RunMetadata(terminalRun);
        terminalMetadata["terminalStatus"] = "completed";
        terminalMetadata["failureCode"] = null;
        terminalMetadata["lifecycleCommitPending"] = false;
        terminalMetadata["terminalTraceSequence"] = terminalEvent.Sequence;
        try
        {
            var audit = new AuditEvent(
                terminalEvent.TimestampUtc,
                terminalRun.AdmissionActor,
                AuditSchema.Actions.LoopRunLifecycle,
                terminalRun.Id,
                AuditSchema.Outcomes.Succeeded,
                "Terminal lifecycle trace is durable.",
                terminalMetadata);
            var recorded = await context.AuditRecorder.RecordOnceAsync(
                GovernedLoopSequentialAuditOperationId.ForTerminalLifecycle(terminalArtifactHash),
                terminalArtifactHash,
                audit,
                IntegrityToken());
            if (recorded.Status is not (GovernedLoopSequentialAuditRecordStatus.Recorded or GovernedLoopSequentialAuditRecordStatus.AlreadyRecorded))
            {
                integrityFailures.Add($"the append-once terminal audit returned `{recorded.Status}`");
            }
        }
        catch (Exception exception)
        {
            integrityFailures.Add($"the append-once terminal audit failed with {SafeExceptionClass(exception)}");
        }

        if (integrityFailures.Count == 0)
        {
            return Result(CustomLoopOrderedRunStatus.Completed, terminalRun, successDetail);
        }

        var warningDetail = $"The truthful canonical Completed trace is durable, but {string.Join(" and ", integrityFailures)}.";
        var warning = Event(terminalRun, Now(terminalRun), CustomLoopRunEventKind.IntegrityWarning, warningDetail);
        try
        {
            var warningPersisted = await _runStore.AppendTerminalIntegrityWarningAsync(terminalRun.Id, terminalRun.LifecycleVersion, warning, IntegrityToken());
            if (warningPersisted.Status == CustomLoopRunStoreStatus.Updated
                && warningPersisted.Run is not null
                && IsExactCanonicalCompletedRun(warningPersisted.Run, context))
            {
                return Result(CustomLoopOrderedRunStatus.NeedsReview, warningPersisted.Run, warningDetail);
            }

            var reconciled = await ReloadExactCanonicalCompletedAsync(context);
            if (reconciled?.Events[^1].Kind == CustomLoopRunEventKind.IntegrityWarning)
            {
                return Result(CustomLoopOrderedRunStatus.NeedsReview, reconciled, reconciled.Events[^1].Detail);
            }

            return Result(CustomLoopOrderedRunStatus.NeedsReview, terminalRun, $"{warningDetail} The one post-terminal integrity warning could not be durably appended ({warningPersisted.Status}).");
        }
        catch (Exception exception)
        {
            return Result(CustomLoopOrderedRunStatus.NeedsReview, terminalRun, $"{warningDetail} The one post-terminal integrity warning persistence outcome is uncertain: {SafeExceptionClass(exception)}.");
        }
    }

    private async Task<CustomLoopRunRecord?> ReloadExactCanonicalCompletedAsync(SequentialExecutionContext context)
    {
        try
        {
            var loaded = await _runStore.GetAsync(context.Anchor.AdapterBinding.ExecutionBinding.RunId, IntegrityToken());
            return IsExactCanonicalCompletedRun(loaded, context) ? loaded : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsExactCanonicalCompletedRun(CustomLoopRunRecord? run, SequentialExecutionContext context)
    {
        if (run is null
            || run.Status != CustomLoopRunStatus.Completed
            || !CustomLoopRunValidator.ValidateForDispatch(run).IsValid
            || !SequentialRunMatches(run, context)
            || !HasCommittedExitCompletion(run)
            || run.Checkpoint.CurrentIterationResult is not { } finalResult
            || !string.Equals(run.FinalOutput, finalResult.Content, StringComparison.Ordinal))
        {
            return false;
        }

        var terminalEvent = GetCanonicalTerminalLifecycleEvent(run);
        if (terminalEvent is null || run.CompletedAtUtc != terminalEvent.TimestampUtc)
        {
            return false;
        }

        var exitCompletions = run.Frontier!.Payload.Nodes
            .Where(activation => activation.Status == GovernedLoopNodeExecutionStatus.Completed
                && activation.Descriptor.Kind == GovernedLoopNodeKind.Exit)
            .Select(activation => FindExactClosedSequentialOutcome(run, activation))
            .Where(item => item is not null
                && item.Sequence <= run.Checkpoint.LastCommittedSequence
                && item.Kind == CustomLoopRunEventKind.ExitDecisionCompleted
                && item.ExitDecision == CustomLoopExitDecision.Complete)
            .ToArray();
        return exitCompletions.Length == 1 && HasExactCanonicalCompletionPublication(run, finalResult);
    }

    private static CustomLoopRunEvent? GetCanonicalTerminalLifecycleEvent(CustomLoopRunRecord run)
    {
        var terminalIndex = run.Events.Length - 1;
        if (terminalIndex >= 0 && run.Events[terminalIndex].Kind == CustomLoopRunEventKind.IntegrityWarning)
        {
            terminalIndex--;
        }

        return terminalIndex >= 0 && run.Events[terminalIndex].Kind == CustomLoopRunEventKind.LifecycleChanged
            ? run.Events[terminalIndex]
            : null;
    }

    private static bool HasExactCanonicalCompletionPublication(CustomLoopRunRecord run, CustomLoopRetainedOutput finalResult)
    {
        CustomLoopContextOutputPolicy outputPolicy;
        try
        {
            outputPolicy = CustomLoopContextResolver.ResolvePolicy(
                run.AdmittedDefinition.ExitPolicy.ContextPolicy,
                run.AdmittedDefinition.ContextDefaults.Exit).ContextOut;
        }
        catch
        {
            return false;
        }

        var operationId = PublicationOperationId(run.Id, run.Checkpoint.Iteration, "exit", isExit: true);
        var intents = run.Events.Where(item => item.Kind == CustomLoopRunEventKind.ConversationPublicationStarted
            && item.Iteration == run.Checkpoint.Iteration
            && string.Equals(item.StepId, "exit", StringComparison.Ordinal)
            && string.Equals(item.ConversationPublicationId, operationId, StringComparison.Ordinal)).ToArray();
        var outcomes = run.Events.Where(item => item.Kind == CustomLoopRunEventKind.ConversationPublished
            && item.Iteration == run.Checkpoint.Iteration
            && string.Equals(item.StepId, "exit", StringComparison.Ordinal)
            && string.Equals(item.ConversationPublicationId, operationId, StringComparison.Ordinal)).ToArray();
        if (!outputPolicy.PublishToInvokingConversation)
        {
            return intents.Length == 0 && outcomes.Length == 0;
        }

        if (outcomes.Length != 1 || outcomes[0].Sequence > run.Checkpoint.LastCommittedSequence)
        {
            return false;
        }

        var outcome = outcomes[0];
        if (run.InvokingConversation is null)
        {
            return intents.Length == 0
                && outcome.PublishedToInvokingConversation == false
                && outcome.CanonicalOutput is null
                && string.Equals(outcome.Detail, PublicationOmittedDetail, StringComparison.Ordinal);
        }

        return intents.Length == 1
            && intents[0].Sequence < outcome.Sequence
            && outcome.PublishedToInvokingConversation == true
            && string.Equals(outcome.CanonicalOutput, finalResult.Content, StringComparison.Ordinal)
            && outcome.OriginalOutputCharacterCount == finalResult.Content.Length
            && outcome.CanonicalOutputTruncated == false
            && (string.Equals(outcome.Detail, PublicationPublishedDetail, StringComparison.Ordinal)
                || string.Equals(outcome.Detail, PublicationAlreadyPublishedDetail, StringComparison.Ordinal));
    }

    private async Task<CustomLoopOrderedRunResult> TerminateAsync(
        CustomLoopRunRecord run,
        string actor,
        CustomLoopRunStatus status,
        string? failureCode,
        string detail,
        string? finalOutput = null,
        bool terminalOutcomeMayExist = true,
        IGovernedLoopSequentialAuditRecorder? terminalAuditRecorder = null)
    {
        if (status == CustomLoopRunStatus.Completed && run.SequentialAdapterBinding is not null)
        {
            return await TerminateAsync(
                run,
                actor,
                CustomLoopRunStatus.NeedsReview,
                "canonical_completion_boundary_bypassed",
                "Canonical successful completion reached a legacy terminal path without its exact sequential completion boundary.");
        }

        var terminalStatus = status;
        if (run.SequentialAdapterBinding is not null && run.Frontier is { } currentFrontier)
        {
            // This is a schema-1 honesty guard, not failure-taxonomy policy. Failed frontier state
            // requires one exact current-node rejection; post-node finalization failures therefore
            // park for review until issue #342 defines their explicit failure-plane classification.
            if (terminalStatus == CustomLoopRunStatus.Failed && !HasExactDefinitiveFrontierOutcome(run, currentFrontier))
            {
                terminalStatus = CustomLoopRunStatus.NeedsReview;
            }
        }

        var now = Now(run);
        var terminalEvent = Event(run, now, CustomLoopRunEventKind.LifecycleChanged, detail);
        var terminalFrontier = ProjectTerminalFrontier(run, terminalStatus, terminalEvent);
        if (run.SequentialAdapterBinding is not null && terminalFrontier is null)
        {
            return Result(CustomLoopOrderedRunStatus.InvalidState, run, "The requested canonical lifecycle terminal could not be composed with the exact durable frontier; no partial terminal write was attempted.");
        }

        var candidate = run with
        {
            LifecycleVersion = run.LifecycleVersion + 1,
            Status = terminalStatus,
            UpdatedAtUtc = now,
            CompletedAtUtc = now,
            ExecutionClock = AdvanceClock(run.ExecutionClock, now, terminal: true),
            Events = [.. run.Events, terminalEvent],
            FinalOutput = terminalStatus == CustomLoopRunStatus.Completed ? finalOutput ?? string.Empty : null,
            FailureCode = terminalStatus is CustomLoopRunStatus.Failed or CustomLoopRunStatus.NeedsReview ? failureCode : null,
            FailureDetail = terminalStatus is CustomLoopRunStatus.Failed or CustomLoopRunStatus.NeedsReview ? detail : null,
            Frontier = terminalFrontier,
        };
        var persisted = await PersistAsync(run, candidate, IntegrityToken(), outcomeMayExist: terminalOutcomeMayExist);
        if (persisted.Run is null)
        {
            return persisted.Terminal ?? Result(CustomLoopOrderedRunStatus.NeedsReview, run, "The terminal trace could not be committed safely.");
        }

        return terminalAuditRecorder is null
            ? await CompleteTerminalLifecycleAuditAsync(persisted.Run, actor, terminalStatus, failureCode, detail, terminalEvent)
            : await CompleteSequentialPureTerminalLifecycleAuditAsync(
                persisted.Run,
                terminalStatus,
                failureCode,
                detail,
                terminalEvent,
                terminalAuditRecorder);
    }

    private async Task<CustomLoopOrderedRunResult> CompleteSequentialPureTerminalLifecycleAuditAsync(
        CustomLoopRunRecord terminalRun,
        CustomLoopRunStatus terminalStatus,
        string? failureCode,
        string detail,
        CustomLoopRunEvent terminalEvent,
        IGovernedLoopSequentialAuditRecorder auditRecorder)
    {
        var resultStatus = terminalStatus switch
        {
            CustomLoopRunStatus.Cancelled => CustomLoopOrderedRunStatus.Cancelled,
            CustomLoopRunStatus.Failed => CustomLoopOrderedRunStatus.Failed,
            CustomLoopRunStatus.NeedsReview => CustomLoopOrderedRunStatus.NeedsReview,
            _ => CustomLoopOrderedRunStatus.InvalidState,
        };
        if (terminalRun.Events.LastOrDefault() is { Kind: CustomLoopRunEventKind.IntegrityWarning } existingWarning)
        {
            return Result(resultStatus, terminalRun, existingWarning.Detail);
        }

        var terminalArtifactHash = CustomLoopSequentialOutcomeArtifactHash.Compute(terminalEvent);
        var terminalMetadata = RunMetadata(terminalRun);
        terminalMetadata["terminalStatus"] = terminalStatus.ToString().ToLowerInvariant();
        terminalMetadata["failureCode"] = failureCode;
        terminalMetadata["lifecycleCommitPending"] = false;
        terminalMetadata["terminalTraceSequence"] = terminalEvent.Sequence;
        string? integrityFailure = null;
        try
        {
            var auditOutcome = terminalStatus switch
            {
                CustomLoopRunStatus.Failed => AuditSchema.Outcomes.Failed,
                CustomLoopRunStatus.NeedsReview => AuditSchema.Outcomes.NeedsReview,
                _ => AuditSchema.Outcomes.Succeeded,
            };
            var audit = new AuditEvent(
                terminalEvent.TimestampUtc.ToUniversalTime(),
                terminalRun.AdmissionActor,
                AuditSchema.Actions.LoopRunLifecycle,
                terminalRun.Id,
                auditOutcome,
                "Terminal lifecycle trace is durable.",
                terminalMetadata);
            var recorded = await auditRecorder.RecordOnceAsync(
                GovernedLoopSequentialAuditOperationId.ForTerminalLifecycle(terminalArtifactHash),
                terminalArtifactHash,
                audit,
                IntegrityToken());
            if (recorded.Status is GovernedLoopSequentialAuditRecordStatus.Recorded or GovernedLoopSequentialAuditRecordStatus.AlreadyRecorded)
            {
                return Result(resultStatus, terminalRun, detail);
            }

            integrityFailure = $"the append-once terminal audit returned `{recorded.Status}`";
        }
        catch (Exception exception)
        {
            integrityFailure = $"the append-once terminal audit failed with {SafeExceptionClass(exception)}";
        }

        var warningDetail = $"The truthful {terminalStatus} terminal trace is durable, but {integrityFailure}.";
        var warning = Event(terminalRun, Now(terminalRun), CustomLoopRunEventKind.IntegrityWarning, warningDetail);
        try
        {
            var warningPersisted = await _runStore.AppendTerminalIntegrityWarningAsync(
                terminalRun.Id,
                terminalRun.LifecycleVersion,
                warning,
                IntegrityToken());
            if (warningPersisted.Status == CustomLoopRunStoreStatus.Updated && warningPersisted.Run is not null)
            {
                return Result(resultStatus, warningPersisted.Run, warningDetail);
            }

            if (warningPersisted.Status is CustomLoopRunStoreStatus.Conflict or CustomLoopRunStoreStatus.TerminalImmutable)
            {
                var latest = await _runStore.GetAsync(terminalRun.Id, IntegrityToken());
                var terminalSnapshot = latest is null ? null : WithoutTerminalIntegrityWarning(latest);
                if (latest?.Events.LastOrDefault() is { Kind: CustomLoopRunEventKind.IntegrityWarning } durableWarning
                    && terminalSnapshot is not null
                    && CustomLoopRunValidator.HasSameDurableVersion(terminalRun, terminalSnapshot))
                {
                    return Result(resultStatus, latest, durableWarning.Detail);
                }
            }

            return Result(resultStatus, terminalRun, $"{warningDetail} The post-terminal integrity warning could not be durably appended ({warningPersisted.Status}).");
        }
        catch (Exception warningException)
        {
            return Result(resultStatus, terminalRun, $"{warningDetail} The post-terminal integrity warning persistence outcome is uncertain: {SafeExceptionClass(warningException)}.");
        }
    }

    private async Task<CustomLoopOrderedRunResult> CompleteTerminalLifecycleAuditAsync(
        CustomLoopRunRecord terminalRun,
        string actor,
        CustomLoopRunStatus terminalStatus,
        string? failureCode,
        string detail,
        CustomLoopRunEvent terminalEvent)
    {
        var resultStatus = terminalStatus switch
        {
            CustomLoopRunStatus.Completed => CustomLoopOrderedRunStatus.Completed,
            CustomLoopRunStatus.Cancelled => CustomLoopOrderedRunStatus.Cancelled,
            CustomLoopRunStatus.Failed => CustomLoopOrderedRunStatus.Failed,
            CustomLoopRunStatus.NeedsReview => CustomLoopOrderedRunStatus.NeedsReview,
            _ => CustomLoopOrderedRunStatus.InvalidState
        };

        var terminalMetadata = RunMetadata(terminalRun);
        terminalMetadata["terminalStatus"] = terminalStatus.ToString().ToLowerInvariant();
        terminalMetadata["failureCode"] = failureCode;
        terminalMetadata["lifecycleCommitPending"] = false;
        terminalMetadata["terminalTraceSequence"] = terminalEvent.Sequence;
        try
        {
            // The terminal trace is already the source of truth. Audit failure cannot roll it back;
            // the fallback appends an integrity warning while preserving the truthful terminal status.
            var auditOutcome = terminalStatus switch
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
            var warningDetail = $"The truthful {terminalStatus} terminal trace is durable, but its terminal audit append failed: {SafeExceptionClass(exception)}.";
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

    private static bool HasExactDefinitiveFrontierOutcome(CustomLoopRunRecord run, GovernedLoopFrontierPosture frontier)
    {
        var current = FindSoleFrontierActivation(
            frontier,
            GovernedLoopNodeExecutionStatus.Running,
            GovernedLoopNodeExecutionStatus.Failed,
            GovernedLoopNodeExecutionStatus.ReviewBlocked);
        var outcome = current is null ? null : FindExactClosedSequentialOutcome(run, current);
        return outcome?.SequentialNodeEvidence is
        {
            Kind: CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection,
            Disposition: CustomLoopSequentialNodeDisposition.Rejected,
        };
    }

    private static CustomLoopRunEvent? FindExactClosedSequentialOutcome(CustomLoopRunRecord run, GovernedLoopNodeExecutionEvidence node)
    {
        if (run.SequentialAdapterBinding is not { } binding
            || node.Attempt is not { } attempt
            || node.AttemptOperationId is not { } attemptOperationId)
        {
            return null;
        }

        var dispatchStarts = run.Events.Where(item =>
            string.Equals(item.EventId, attemptOperationId, StringComparison.Ordinal)
            && item.Attempt == attempt
            && item.SequentialNodeEvidence is
            {
                Kind: CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
                Disposition: CustomLoopSequentialNodeDisposition.Unknown,
            } evidence
            && EvidenceMatchesActivation(evidence, binding, node, attempt)
            && CustomLoopSequentialNodeEvidenceHash.Matches(evidence)
            && CustomLoopSequentialOutcomeArtifactHash.Matches(item)).ToArray();
        if (dispatchStarts.Length != 1)
        {
            return null;
        }

        var dispatchStart = dispatchStarts[0];
        var matches = run.Events.Where(item => item.SequentialNodeEvidence is { } evidence
            && item.Sequence > dispatchStart.Sequence
            && item.Attempt == attempt
            && EvidenceMatchesActivation(evidence, binding, node, attempt)
            && IsClosedSequentialOutcome(evidence.Kind, evidence.Disposition)
            && CustomLoopSequentialNodeEvidenceHash.Matches(evidence)
            && CustomLoopSequentialOutcomeArtifactHash.Matches(item)
            && SequentialEvidenceEventMatchesNode(item, node.Descriptor.Kind)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool EvidenceMatchesActivation(
        CustomLoopSequentialNodeEvidence evidence,
        GovernedLoopSequentialAdapterBinding binding,
        GovernedLoopNodeExecutionEvidence activation,
        int attempt)
        => evidence.ActivationOrdinal == activation.ActivationOrdinal
            && evidence.VisitOrdinal == activation.VisitOrdinal
            && string.Equals(evidence.NodeId, activation.NodeId, StringComparison.Ordinal)
            && evidence.Attempt == attempt
            && string.Equals(evidence.CycleId, activation.CycleId, StringComparison.Ordinal)
            && evidence.CycleIteration == activation.CycleIteration
            && string.Equals(evidence.WorkspaceId, binding.WorkspaceId, StringComparison.Ordinal)
            && string.Equals(evidence.RunId, binding.ExecutionBinding.RunId, StringComparison.Ordinal)
            && Equals(evidence.Revision, binding.ExecutionBinding.Revision)
            && evidence.ExecutionGeneration == binding.ExecutionBinding.ExecutionGeneration;

    private static GovernedLoopNodeExecutionEvidence? FindSoleFrontierActivation(
        GovernedLoopFrontierPosture frontier,
        params GovernedLoopNodeExecutionStatus[] statuses)
    {
        var matches = frontier.Payload.Nodes
            .Where(candidate => statuses.Contains(candidate.Status))
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool IsClosedSequentialOutcome(CustomLoopSequentialNodeEvidenceKind kind, CustomLoopSequentialNodeDisposition disposition)
        => (kind, disposition) is
            (CustomLoopSequentialNodeEvidenceKind.CompletedOutcome, CustomLoopSequentialNodeDisposition.Completed)
            or (CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection, CustomLoopSequentialNodeDisposition.Rejected)
            or (CustomLoopSequentialNodeEvidenceKind.AmbiguityAttention, CustomLoopSequentialNodeDisposition.NeedsReview);

    private static GovernedLoopFrontierPosture? ProjectTerminalFrontier(
        CustomLoopRunRecord run,
        CustomLoopRunStatus status,
        CustomLoopRunEvent terminalEvent)
    {
        if (run.SequentialAdapterBinding is not { } binding || run.Frontier is not { } frontier)
        {
            return run.Frontier;
        }

        if (status == CustomLoopRunStatus.Completed)
        {
            return frontier.Payload.Status == GovernedLoopFrontierStatus.Completed ? frontier : null;
        }

        if (status == CustomLoopRunStatus.Cancelled)
        {
            if (frontier.Payload.Status == GovernedLoopFrontierStatus.Cancelled)
            {
                return frontier;
            }

            var cancelled = GovernedLoopSequentialFrontierMachine.CancelCurrent(frontier, binding, terminalEvent.TimestampUtc);
            return cancelled.Status == GovernedLoopSequentialFrontierTransitionStatus.Applied ? cancelled.Frontier : null;
        }

        if (status is not (CustomLoopRunStatus.Failed or CustomLoopRunStatus.NeedsReview))
        {
            return frontier;
        }

        if (status == CustomLoopRunStatus.NeedsReview && frontier.Payload.Status != GovernedLoopFrontierStatus.Active)
        {
            return frontier.Payload.Status == GovernedLoopFrontierStatus.ReviewBlocked ? frontier : null;
        }

        if (frontier.Payload.Status == GovernedLoopFrontierStatus.Failed)
        {
            return frontier;
        }

        if (frontier.Payload.Status is GovernedLoopFrontierStatus.Completed or GovernedLoopFrontierStatus.Cancelled)
        {
            return null;
        }

        var current = FindSoleFrontierActivation(
            frontier,
            GovernedLoopNodeExecutionStatus.Running,
            GovernedLoopNodeExecutionStatus.ReviewBlocked);
        var exactOutcome = current is null ? null : FindExactClosedSequentialOutcome(run, current);
        var exactEvidence = exactOutcome?.SequentialNodeEvidence;
        GovernedLoopSequentialFrontierTransitionResult transition;
        if (status == CustomLoopRunStatus.NeedsReview && current?.Status == GovernedLoopNodeExecutionStatus.Running)
        {
            transition = GovernedLoopSequentialFrontierMachine.ReviewBlockCurrent(
                frontier,
                binding,
                exactOutcome?.EventId,
                exactEvidence?.OutcomeArtifactHash,
                exactEvidence?.ControlOutcome,
                exactEvidence?.SelectedControlEdgeIds,
                exactEvidence?.SkippedControlEdgeIds,
                terminalEvent.TimestampUtc);
        }
        else if (status == CustomLoopRunStatus.NeedsReview
            && current is null
            && frontier.Payload.Nodes.Any(candidate => candidate.Status == GovernedLoopNodeExecutionStatus.Ready))
        {
            transition = GovernedLoopSequentialFrontierMachine.ReviewBlockAggregate(
                frontier,
                binding,
                terminalEvent.TimestampUtc);
        }
        else if (status == CustomLoopRunStatus.Failed && current is not null && exactOutcome?.SequentialNodeEvidence is
        {
            Kind: CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection,
            Disposition: CustomLoopSequentialNodeDisposition.Rejected,
        })
        {
            transition = GovernedLoopSequentialFrontierMachine.FailCurrent(
                frontier,
                binding,
                current.AttemptOperationId,
                exactOutcome.EventId,
                exactOutcome.SequentialNodeEvidence!.OutcomeArtifactHash,
                exactOutcome.SequentialNodeEvidence.ControlOutcome,
                exactOutcome.SequentialNodeEvidence.SelectedControlEdgeIds,
                exactOutcome.SequentialNodeEvidence.SkippedControlEdgeIds,
                terminalEvent.TimestampUtc);
        }
        else
        {
            return null;
        }

        return transition.Status == GovernedLoopSequentialFrontierTransitionStatus.Applied ? transition.Frontier : null;
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
                var reviewFrontier = ProjectTerminalFrontier(latest, CustomLoopRunStatus.NeedsReview, lifecycle);
                if (latest.SequentialAdapterBinding is not null && reviewFrontier is null)
                {
                    return new RunAdvance(null, Result(CustomLoopOrderedRunStatus.NeedsReview, latest, $"{detail} The exact durable frontier could not be composed with NeedsReview."));
                }

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
                    FailureDetail = detail,
                    Frontier = reviewFrontier,
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
        int? traceReservationUtf8Bytes = null,
        string? eventId = null,
        string? pureNodeOutcomeJson = null)
    {
        return new CustomLoopRunEvent(run.Events.Length + 1, eventId ?? NewCorrelationId("event"), now, kind, iteration, stepId, attempt, detail, contextBlocks ?? [], output, originalOutputCharacters, truncated, retained, published, publicationId, provider, model, providerResponseId, exitDecision, toolAuthority, toolEvidence, traceReservationUtf8Bytes)
        {
            PureNodeOutcomeJson = pureNodeOutcomeJson,
        };
    }

    private static CustomLoopRunEvent WithSequentialEvidence(
        CustomLoopRunEvent runEvent,
        SequentialNodeExecutionContext context,
        CustomLoopSequentialNodeEvidenceKind kind,
        CustomLoopSequentialNodeDisposition disposition)
        => WithSequentialEvidenceCore(runEvent, context, kind, disposition, null, deriveControlOutcome: true);

    private static CustomLoopRunEvent WithSequentialEvidence(
        CustomLoopRunEvent runEvent,
        SequentialNodeExecutionContext context,
        CustomLoopSequentialNodeEvidenceKind kind,
        CustomLoopSequentialNodeDisposition disposition,
        GovernedLoopControlCondition controlOutcome)
        => WithSequentialEvidenceCore(runEvent, context, kind, disposition, controlOutcome, deriveControlOutcome: false);

    private static CustomLoopRunEvent WithSequentialEvidenceCore(
        CustomLoopRunEvent runEvent,
        SequentialNodeExecutionContext context,
        CustomLoopSequentialNodeEvidenceKind kind,
        CustomLoopSequentialNodeDisposition disposition,
        GovernedLoopControlCondition? explicitControlOutcome,
        bool deriveControlOutcome)
    {
        var binding = context.Binding;
        var controlOutcome = deriveControlOutcome ? kind switch
        {
            CustomLoopSequentialNodeEvidenceKind.CompletedOutcome => GovernedLoopControlCondition.Success,
            CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection => GovernedLoopControlCondition.Failure,
            _ => (GovernedLoopControlCondition?)null,
        } : explicitControlOutcome;
        var selectedControlEdgeIds = controlOutcome is null
            ? []
            : context.Artifact.Graph.ControlEdges
                .Where(edge => context.Activation.OutgoingControlEdgeIds.Contains(edge.Id, StringComparer.Ordinal)
                    && edge.Condition == controlOutcome)
                .Select(edge => edge.Id)
                .Order(StringComparer.Ordinal)
                .ToArray();
        var skippedControlEdgeIds = controlOutcome is null
            ? []
            : context.Activation.OutgoingControlEdgeIds
                .Except(selectedControlEdgeIds, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
        var evidence = CustomLoopSequentialNodeEvidenceHash.Apply(new CustomLoopSequentialNodeEvidence(
            CustomLoopSequentialNodeEvidence.CurrentSchemaVersion,
            kind,
            binding.WorkspaceId,
            binding.ExecutionBinding.RunId,
            binding.ExecutionBinding.Revision,
            binding.ExecutionBinding.ExecutionGeneration,
            context.Activation.ActivationOrdinal,
            context.Activation.VisitOrdinal,
            context.Node.NodeId,
            context.Attempt,
            context.Activation.CycleId,
            context.Activation.CycleIteration,
            controlOutcome,
            selectedControlEdgeIds,
            skippedControlEdgeIds,
            null,
            null,
            disposition,
            CustomLoopSequentialOutcomeArtifactHash.Compute(runEvent),
            string.Empty));
        return runEvent with { SequentialNodeEvidence = evidence };
    }

    private static CustomLoopRunEvent WithSequentialSkipEvidence(
        CustomLoopRunEvent runEvent,
        GovernedLoopSequentialAdapterBinding binding,
        GovernedLoopSequentialPrunedActivation pruning)
    {
        var activation = pruning.Activation;
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

    private static AuditEvent CreatePureNodeAudit(
        CustomLoopRunRecord run,
        string actor,
        CustomLoopRunEvent runEvent,
        string outcome,
        GovernedLoopPureNodeOutcome? pureOutcome)
    {
        var evidence = runEvent.SequentialNodeEvidence
            ?? throw new InvalidOperationException("Pure-node audit requires exact sequential evidence.");
        var metadata = RunMetadata(run);
        metadata["iteration"] = runEvent.Iteration;
        metadata["stepId"] = runEvent.StepId;
        metadata["attempt"] = runEvent.Attempt;
        metadata["canonicalNodeId"] = evidence.NodeId;
        metadata["sequentialEvidenceHash"] = evidence.EvidenceHash;
        metadata["modelDispatched"] = false;
        metadata["pureNodeOutcomeHash"] = pureOutcome?.ContentHash;
        metadata["validationPassed"] = pureOutcome?.ValidationEvidence?.Passed;
        return new AuditEvent(
            runEvent.TimestampUtc.ToUniversalTime(),
            actor,
            AuditSchema.Actions.LoopNodeAttempt,
            run.Id,
            outcome,
            pureOutcome is null
                ? "Deterministic pure-node execution reached a bounded dispatch disposition."
                : "Deterministic pure-node outcome evidence is durable.",
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

    private static Task<SequentialAuditBoundaryFailure?> ReconcilePureNodeStartAuditAsync(
        CustomLoopRunRecord run,
        GovernedLoopSequentialPlanNode node,
        GovernedLoopNodeExecutionEvidence activation,
        int attempt,
        string attemptOperationId,
        string actor,
        IGovernedLoopSequentialAuditRecorder auditRecorder)
    {
        var start = FindSequentialDispatchStart(run, node, activation, attempt, attemptOperationId);
        return start is null
            ? Task.FromResult<SequentialAuditBoundaryFailure?>(new SequentialAuditBoundaryFailure(
                "canonical_start_audit_conflict",
                "The deterministic pure-node attempt has no exact authenticated start marker for append-once audit reconciliation."))
            : AppendPureNodeStartAuditAsync(
                run,
                start,
                CreatePureNodeAudit(run, actor, start, AuditSchema.Outcomes.Started, null),
                auditRecorder,
                IntegrityToken());
    }

    private static async Task<SequentialAuditBoundaryFailure?> AppendPureNodeStartAuditAsync(
        CustomLoopRunRecord run,
        CustomLoopRunEvent startEvent,
        AuditEvent auditEvent,
        IGovernedLoopSequentialAuditRecorder auditRecorder,
        CancellationToken cancellationToken)
    {
        var evidence = startEvent.SequentialNodeEvidence;
        if (startEvent.Kind != CustomLoopRunEventKind.NodeAttemptStarted
            || evidence is not
            {
                Kind: CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
                Disposition: CustomLoopSequentialNodeDisposition.Unknown,
            }
            || !CustomLoopSequentialNodeEvidenceHash.Matches(evidence)
            || !CustomLoopSequentialOutcomeArtifactHash.Matches(startEvent))
        {
            return new SequentialAuditBoundaryFailure(
                "canonical_start_audit_conflict",
                "The deterministic pure-node start could not identify one exact append-once audit operation.");
        }

        GovernedLoopSequentialAuditRecordResult? recorded;
        try
        {
            recorded = await auditRecorder.RecordOnceAsync(
                GovernedLoopSequentialAuditOperationId.ForNodeStart(evidence.EvidenceHash),
                evidence.EvidenceHash,
                auditEvent with
                {
                    TimestampUtc = startEvent.TimestampUtc.ToUniversalTime(),
                    Actor = run.AdmissionActor,
                },
                cancellationToken);
        }
        catch (Exception exception)
        {
            return new SequentialAuditBoundaryFailure(
                "canonical_start_audit_unavailable",
                $"The append-once deterministic pure-node start audit could not prove durability: {SafeExceptionClass(exception)}.");
        }

        return recorded?.Status switch
        {
            GovernedLoopSequentialAuditRecordStatus.Recorded or GovernedLoopSequentialAuditRecordStatus.AlreadyRecorded => null,
            GovernedLoopSequentialAuditRecordStatus.Conflict => new SequentialAuditBoundaryFailure(
                "canonical_start_audit_conflict",
                "The append-once deterministic pure-node start audit operation is already bound to divergent evidence."),
            _ => new SequentialAuditBoundaryFailure(
                "canonical_start_audit_unavailable",
                "The append-once deterministic pure-node start audit could not prove a durable result."),
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
        if (start.Kind == CustomLoopRunEventKind.NodeAttemptStarted
            && start.TraceReservationUtf8Bytes == CustomLoopLimits.MaxGraphPureNodeOutcomeEvidenceReservationUtf8Bytes
            && start.SequentialNodeEvidence is { Kind: CustomLoopSequentialNodeEvidenceKind.DispatchStarted })
        {
            return false;
        }

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
        => CustomLoopRunValidator.HasSameDurableVersion(expected, actual);

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
            || !GovernedLoopSequentialFrontierMachine.Validate(run.Frontier, binding, context.Plan)
            || !string.Equals(definition.RoleId, graph.OwningRole.Identity.RoleId, StringComparison.Ordinal)
            || definition.InferenceSteps.Length != context.Plan.Nodes.Count(item => Equals(item.Descriptor, GovernedLoopSequentialNodeDescriptors.ProviderInference))
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

    private static bool HasOpenSequentialPureAttempt(CustomLoopRunRecord run, SequentialExecutionContext context)
    {
        var selected = GovernedLoopSequentialFrontierMachine.Select(run.Frontier, context.Anchor.AdapterBinding, context.Plan);
        return selected is
        {
            Status: GovernedLoopSequentialFrontierSelectionStatus.Running,
            Node: { } node,
            Activation: { } activation,
            Attempt: { } attempt,
            AttemptOperationId: { } attemptOperationId,
        }
            && GovernedLoopSequentialNodeDescriptors.IsPure(node.Descriptor)
            && FindSequentialDispatchStart(run, node, activation, attempt, attemptOperationId) is not null
            && FindSequentialNodeEvidence(run, node, activation, attempt) is null;
    }

    private static bool HasExactOpenPureAttempt(CustomLoopRunRecord run, SequentialNodeExecutionContext context)
        => run.Frontier?.Payload.Nodes.ElementAtOrDefault(context.Activation.ActivationOrdinal) is
        {
            Status: GovernedLoopNodeExecutionStatus.Running,
            Attempt: { } attempt,
            AttemptOperationId: { } attemptOperationId,
        } node
            && string.Equals(node.NodeId, context.Node.NodeId, StringComparison.Ordinal)
            && node.ActivationOrdinal == context.Activation.ActivationOrdinal
            && node.VisitOrdinal == context.Activation.VisitOrdinal
            && attempt == context.Attempt
            && string.Equals(attemptOperationId, context.AttemptOperationId, StringComparison.Ordinal)
            && FindSequentialDispatchStart(run, context.Node, context.Activation, context.Attempt, context.AttemptOperationId) is not null
            && FindSequentialNodeEvidence(run, context.Node, context.Activation, context.Attempt) is null;

    private static CustomLoopRunEvent? FindSequentialNodeEvidence(
        CustomLoopRunRecord run,
        GovernedLoopSequentialPlanNode node,
        GovernedLoopNodeExecutionEvidence activation,
        int attempt)
    {
        if (run.SequentialAdapterBinding is not { } binding)
        {
            return null;
        }

        var matches = run.Events.Where(item => item.SequentialNodeEvidence is { Kind: not CustomLoopSequentialNodeEvidenceKind.DispatchStarted } evidence
            && evidence.ActivationOrdinal == activation.ActivationOrdinal
            && evidence.VisitOrdinal == activation.VisitOrdinal
            && string.Equals(evidence.NodeId, node.NodeId, StringComparison.Ordinal)
            && evidence.Attempt == attempt
            && string.Equals(evidence.CycleId, activation.CycleId, StringComparison.Ordinal)
            && evidence.CycleIteration == activation.CycleIteration
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

    private static bool HasSequentialTerminalCandidate(
        CustomLoopRunRecord run,
        GovernedLoopSequentialPlanNode node,
        GovernedLoopNodeExecutionEvidence activation,
        int attempt)
        => run.Events.Any(item => (item.Kind is CustomLoopRunEventKind.NodeAttemptCompleted or CustomLoopRunEventKind.NodeAttemptFailed)
            && item.Attempt == attempt
            && item.SequentialNodeEvidence?.ActivationOrdinal == activation.ActivationOrdinal
            && item.SequentialNodeEvidence?.VisitOrdinal == activation.VisitOrdinal
            && (string.Equals(item.StepId, node.NodeId, StringComparison.Ordinal)
                || string.Equals(item.SequentialNodeEvidence?.NodeId, node.NodeId, StringComparison.Ordinal)));

    private static bool SequentialEvidenceEventMatchesNode(
        CustomLoopRunEvent runEvent,
        EmbodySense.Core.Common.Loops.Models.Custom.Graph.GovernedLoopNodeKind nodeKind)
        => nodeKind switch
        {
            EmbodySense.Core.Common.Loops.Models.Custom.Graph.GovernedLoopNodeKind.Trigger
                => runEvent.Kind == CustomLoopRunEventKind.Admitted,
            EmbodySense.Core.Common.Loops.Models.Custom.Graph.GovernedLoopNodeKind.Inference
                => runEvent.Kind is CustomLoopRunEventKind.NodeAttemptCompleted or CustomLoopRunEventKind.NodeOutcomeObserved or CustomLoopRunEventKind.NodeAttemptFailed,
            EmbodySense.Core.Common.Loops.Models.Custom.Graph.GovernedLoopNodeKind.Transform
                or EmbodySense.Core.Common.Loops.Models.Custom.Graph.GovernedLoopNodeKind.Validate
                or EmbodySense.Core.Common.Loops.Models.Custom.Graph.GovernedLoopNodeKind.Condition
                or EmbodySense.Core.Common.Loops.Models.Custom.Graph.GovernedLoopNodeKind.Join
                => runEvent.Kind is CustomLoopRunEventKind.NodeAttemptCompleted or CustomLoopRunEventKind.NodeAttemptFailed,
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
        PendingTerminal? PendingTerminal = null,
        CustomLoopRunRecord? AuthenticatedPureOutcomeSnapshot = null);

    private sealed record PendingTerminal(
        CustomLoopRunStatus Status,
        string? FailureCode,
        string Detail,
        string? FinalOutput);

    private sealed record SequentialAuditBoundaryFailure(
        string FailureCode,
        string Detail);

    private sealed record SequentialFrontierCompletion(
        GovernedLoopFrontierPosture Frontier,
        IReadOnlyList<CustomLoopRunEvent> SkipEvents);

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
        GovernedLoopNodeExecutionEvidence Activation,
        int Attempt,
        string AttemptOperationId,
        IReadOnlyList<CapabilityId> AllowedCapabilityIds,
        IGovernedLoopSequentialAuditRecorder AuditRecorder);

    private sealed record CanonicalOutput(string Text, int OriginalCharacterCount, bool Truncated);

}
