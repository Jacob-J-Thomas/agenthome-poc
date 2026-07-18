using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Governance.Audit.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

public sealed class CustomLoopOrderedRunner : ICustomLoopResumeExecutor, ICustomLoopExecutionCancellationSignal
{
    private static readonly TimeSpan IntegrityWriteTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions TraceSizingJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    private readonly ICustomLoopRunStore _runStore;
    private readonly CustomLoopContextResolver _contextResolver;
    private readonly ICustomLoopInferenceAttemptExecutor _inferenceExecutor;
    private readonly ICustomLoopConversationPublisher _conversationPublisher;
    private readonly IAuditLog _auditLog;
    private readonly ICustomLoopToolAuthorityProvider _authorityProvider;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeAttemptCancellations = new(StringComparer.Ordinal);

    public CustomLoopOrderedRunner(
        ICustomLoopRunStore runStore,
        CustomLoopContextResolver contextResolver,
        ICustomLoopInferenceAttemptExecutor inferenceExecutor,
        ICustomLoopConversationPublisher conversationPublisher,
        IAuditLog auditLog,
        ICustomLoopToolAuthorityProvider authorityProvider,
        TimeProvider? timeProvider = null)
    {
        _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
        _contextResolver = contextResolver ?? throw new ArgumentNullException(nameof(contextResolver));
        _inferenceExecutor = inferenceExecutor ?? throw new ArgumentNullException(nameof(inferenceExecutor));
        _conversationPublisher = conversationPublisher ?? throw new ArgumentNullException(nameof(conversationPublisher));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _authorityProvider = authorityProvider ?? throw new ArgumentNullException(nameof(authorityProvider));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<CustomLoopOrderedRunResult> RunAsync(CustomLoopOrderedRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Actor);

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
            return Result(CustomLoopOrderedRunStatus.Failed, null, $"The run trace could not be loaded safely: {SafeExceptionClass(exception)}.");
        }

        if (run is null)
        {
            return Result(CustomLoopOrderedRunStatus.NotFound, null, "The custom-loop run does not exist.");
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
            if (cancellationToken.IsCancellationRequested)
            {
                return await CancelBeforeDispatchAsync(run, request.Actor);
            }

            var started = await StartRunAsync(run, request.Actor, cancellationToken);
            if (started.Terminal is not null)
            {
                return started.Terminal;
            }

            run = started.Run!;
        }
        else
        {
            return Result(CustomLoopOrderedRunStatus.InvalidState, run, "Public execution starts only from Admitted. Interrupted runs require explicit recovery to Paused and a separate authenticated Resume path.");
        }

        return await ContinueAsync(run, request.Actor, cancellationToken);
    }

    public async Task<CustomLoopOrderedRunResult> ResumeAsync(CustomLoopResumeExecutionRequest request, CancellationToken cancellationToken = default)
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

        return await ContinueAsync(run, request.Actor, cancellationToken);
    }

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
        }
    }

    private async Task<CustomLoopOrderedRunResult> ContinueAsync(CustomLoopRunRecord run, string actor, CancellationToken cancellationToken)
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
                var advanced = await ExecuteInferenceStepAsync(run, step, actor, cancellationToken);
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
                return await CompleteDeterministicallyAsync(run, actor, "Continuation is disabled; Exit completed without a model call.", cancellationToken);
            }

            if (run.Checkpoint.AcceptedRepeatCount >= exit.MaxAdditionalIterations)
            {
                return await CompleteDeterministicallyAsync(run, actor, "The repeat ceiling was reached; Exit completed without a model call.", cancellationToken);
            }

            var exitAdvance = await ExecuteExitAsync(run, actor, cancellationToken);
            if (exitAdvance.Terminal is not null)
            {
                return exitAdvance.Terminal;
            }

            run = exitAdvance.Run!;
        }
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

    private async Task<RunAdvance> ExecuteInferenceStepAsync(CustomLoopRunRecord run, CustomLoopInferenceStep step, string actor, CancellationToken cancellationToken)
    {
        CustomLoopContextAssembly assembly;
        CustomLoopToolAuthoritySnapshot authority;
        try
        {
            authority = await _authorityProvider.ResolveAsync(run.AdmittedDefinition.RoleId, run.AdmittedDefinition.ToolAssignments, cancellationToken);
            EnsureAuthorityBound(run, authority, run.AdmittedDefinition.ToolAssignments);

            assembly = _contextResolver.ResolveInference(run, step, authority.EffectiveAssignments);
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
        events.Add(Event(sequenceOwner, now, CustomLoopRunEventKind.NodeAttemptStarted, "Inference attempt trace committed before provider dispatch.", iteration, step.Id, 1, assembly.Blocks, provider: run.ModelSnapshot.Provider, model: run.ModelSnapshot.Model, providerResponseId: correlation, toolAuthority: authority, traceReservationUtf8Bytes: CustomLoopLimits.MaxAttemptEvidenceReservationUtf8Bytes));
        var startedCandidate = Append(run, now, events);
        if (!HasTraceCapacityForDispatch(startedCandidate))
        {
            var terminal = await TerminateAsync(run, actor, CustomLoopRunStatus.Failed, "run_trace_capacity_exhausted", "The durable run trace cannot reserve enough bounded space for another provider attempt and its mandatory outcome evidence.");
            return new RunAdvance(terminal.Run, terminal);
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
            await _auditLog.AppendAsync(AttemptAudit(actor, run, step.Id, iteration, correlation, assembly, AuditSchema.Actions.LoopNodeAttempt, AuditSchema.Outcomes.Started, null, null), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var cancelled = await CancelBeforeDispatchAsync(run, actor);
            return new RunAdvance(cancelled.Run, cancelled);
        }
        catch (Exception exception)
        {
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
            1,
            correlation,
            IsExit: false,
            AllowTools: authority.EffectiveAssignments.Length > 0,
            run.ModelSnapshot,
            assignments,
            run.Checkpoint.ToolRequestsUsed,
            assembly.Request,
            authority);

        CustomLoopInferenceAttemptResult result;
        var providerInvoked = false;
        using var providerToken = CreateProviderToken(run, cancellationToken);
        if (!_activeAttemptCancellations.TryAdd(run.Id, providerToken))
        {
            return await RecordAttemptFailureAsync(run, actor, step.Id, iteration, correlation, assembly, new InvalidOperationException("A provider attempt is already registered for this run."), isExit: false);
        }

        try
        {
            var dispatchBoundary = await ObserveControlBoundaryAsync(run, actor);
            if (dispatchBoundary.Terminal is not null)
            {
                return dispatchBoundary;
            }

            run = dispatchBoundary.Run!;
            if (ExecutionDeadlineReached(run))
            {
                var terminal = await TerminateAsync(run, actor, CustomLoopRunStatus.Failed, "run_deadline_exceeded", "The custom-loop execution deadline was reached before the provider request could start.");
                return new RunAdvance(terminal.Run, terminal);
            }

            providerToken.Token.ThrowIfCancellationRequested();
            providerInvoked = true;
            result = await _inferenceExecutor.ExecuteAsync(attemptRequest, providerToken.Token);
        }
        catch (OperationCanceledException) when (!providerInvoked)
        {
            return await HandlePreInvocationCancellationAsync(run, actor, cancellationToken);
        }
        catch (Exception exception)
        {
            return await RecordAttemptFailureAsync(run, actor, step.Id, iteration, correlation, assembly, exception, isExit: false);
        }
        finally
        {
            _activeAttemptCancellations.TryRemove(run.Id, out _);
        }

        if (result is null)
        {
            return await RecordAttemptFailureAsync(run, actor, step.Id, iteration, correlation, assembly, new InvalidOperationException("Provider executor returned no result."), isExit: false);
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
        var observed = Event(run, observedNow, CustomLoopRunEventKind.NodeOutcomeObserved, "Inference provider outcome was observed and retained as local evidence.", iteration, step.Id, 1, output: canonical.Text, originalOutputCharacters: canonical.OriginalCharacterCount, truncated: canonical.Truncated, retained: assembly.ResolvedOutputPolicy.RetainForLoopReasoning, published: assembly.ResolvedOutputPolicy.PublishToInvokingConversation, publicationId: publicationId, provider: run.ModelSnapshot.Provider, model: run.ModelSnapshot.Model, providerResponseId: safeProviderResponseId);
        var completed = Event(run with { Events = [.. run.Events, observed] }, observedNow, CustomLoopRunEventKind.NodeAttemptCompleted, "Inference attempt completed without an automatic retry.", iteration, step.Id, 1, output: canonical.Text, originalOutputCharacters: canonical.OriginalCharacterCount, truncated: canonical.Truncated, retained: assembly.ResolvedOutputPolicy.RetainForLoopReasoning, published: assembly.ResolvedOutputPolicy.PublishToInvokingConversation, publicationId: publicationId, provider: run.ModelSnapshot.Provider, model: run.ModelSnapshot.Model, providerResponseId: safeProviderResponseId);
        var observedCandidate = Append(run, observedNow, [observed, completed]);
        var observedPersisted = await PersistAsync(run, observedCandidate, IntegrityToken(), outcomeMayExist: true);
        if (observedPersisted.Terminal is not null)
        {
            return observedPersisted;
        }

        run = observedPersisted.Run!;
        var integrityError = ValidateProviderResult(run, result);
        try
        {
            var outcome = integrityError is null ? AuditSchema.Outcomes.Succeeded : AuditSchema.Outcomes.NeedsReview;
            await _auditLog.AppendAsync(AttemptAudit(actor, run, step.Id, iteration, correlation, assembly, AuditSchema.Actions.LoopNodeAttempt, outcome, canonical, result), IntegrityToken());
        }
        catch (Exception exception)
        {
            var terminal = await TerminateAsync(run, actor, CustomLoopRunStatus.NeedsReview, "attempt_outcome_audit_failed", $"The provider outcome is evidence, but its matching audit could not be recorded: {SafeExceptionClass(exception)}.");
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
            : await PublishIfSelectedAsync(run, assembly.ResolvedOutputPolicy, retained, step.Id, isExit: false, actor);
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
            ToolRequestsUsed = checked(run.Checkpoint.ToolRequestsUsed + result.ToolRequestsConsumed)
        };
        return await CommitCheckpointAsync(run, checkpoint, $"Inference checkpoint committed after `{step.Id}`.");
    }

    private async Task<RunAdvance> ExecuteExitAsync(CustomLoopRunRecord run, string actor, CancellationToken cancellationToken)
    {
        CustomLoopContextAssembly assembly;
        CustomLoopToolAuthoritySnapshot authority;
        try
        {
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
        if (!HasTraceCapacityForDispatch(startedCandidate))
        {
            var terminal = await TerminateAsync(run, actor, CustomLoopRunStatus.Failed, "run_trace_capacity_exhausted", "The durable run trace cannot reserve enough bounded space for another Exit attempt and its mandatory outcome evidence.");
            return new RunAdvance(terminal.Run, terminal);
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
            authority);

        CustomLoopInferenceAttemptResult result;
        var providerInvoked = false;
        using var providerToken = CreateProviderToken(run, cancellationToken);
        if (!_activeAttemptCancellations.TryAdd(run.Id, providerToken))
        {
            return await RecordAttemptFailureAsync(run, actor, "exit", iteration, correlation, assembly, new InvalidOperationException("A provider attempt is already registered for this run."), isExit: true);
        }

        try
        {
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
            providerInvoked = true;
            result = await _inferenceExecutor.ExecuteAsync(attemptRequest, providerToken.Token);
        }
        catch (OperationCanceledException) when (!providerInvoked)
        {
            return await HandlePreInvocationCancellationAsync(run, actor, cancellationToken);
        }
        catch (Exception exception)
        {
            return await RecordAttemptFailureAsync(run, actor, "exit", iteration, correlation, assembly, exception, isExit: true);
        }
        finally
        {
            _activeAttemptCancellations.TryRemove(run.Id, out _);
        }

        if (result is null)
        {
            return await RecordAttemptFailureAsync(run, actor, "exit", iteration, correlation, assembly, new InvalidOperationException("Provider executor returned no result."), isExit: true);
        }

        var refreshed = await RefreshControlUpdateAsync(run);
        if (refreshed.Terminal is not null)
        {
            return refreshed;
        }

        run = refreshed.Run!;

        var canonical = Canonicalize(result.OutputText);
        var decision = ParseExitDecision(canonical.Text);
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
        var integrityError = ValidateProviderResult(run, result);
        if (integrityError is null && result.ToolRequestsConsumed != 0)
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
        var iterationResult = run.Checkpoint.CurrentIterationResult;
        if (iterationResult is null)
        {
            return await TerminateAsync(run, actor, CustomLoopRunStatus.Failed, "missing_iteration_result", "Deterministic Exit could not find the final inference result.");
        }

        CustomLoopContextOutputPolicy outputPolicy;
        try
        {
            outputPolicy = CustomLoopContextResolver.ResolvePolicy(run.AdmittedDefinition.ExitPolicy.ContextPolicy, run.AdmittedDefinition.ContextDefaults.Exit).ContextOut;
        }
        catch (Exception exception)
        {
            return await TerminateAsync(run, actor, CustomLoopRunStatus.Failed, "invalid_exit_policy", $"The deterministic Exit policy is invalid: {SafeExceptionClass(exception)}.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return await CancelBeforeDispatchAsync(run, actor);
        }

        var exitEvent = Event(
            run,
            Now(run),
            CustomLoopRunEventKind.ExitDecisionCompleted,
            detail,
            run.Checkpoint.Iteration,
            "exit",
            1,
            retained: outputPolicy.RetainForLoopReasoning,
            published: outputPolicy.PublishToInvokingConversation,
            publicationId: outputPolicy.PublishToInvokingConversation ? PublicationOperationId(run.Id, run.Checkpoint.Iteration, "exit", isExit: true) : null,
            exitDecision: CustomLoopExitDecision.Complete);
        var exitCandidate = Append(run, exitEvent.TimestampUtc, [exitEvent]);
        RunAdvance exitPersisted;
        try
        {
            exitPersisted = await PersistAsync(run, exitCandidate, cancellationToken, outcomeMayExist: false, propagateCancellation: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await CancelAfterInterruptedPreDispatchPersistenceAsync(run, exitCandidate, actor);
        }

        if (exitPersisted.Terminal is not null)
        {
            return exitPersisted.Terminal;
        }

        run = exitPersisted.Run!;
        try
        {
            var metadata = RunMetadata(run);
            metadata["iteration"] = run.Checkpoint.Iteration;
            metadata["decision"] = "complete";
            metadata["modelDispatched"] = false;
            await _auditLog.AppendAsync(AuditEvent.Create(actor, AuditSchema.Actions.LoopExitDecision, run.Id, AuditSchema.Outcomes.Succeeded, detail, metadata), IntegrityToken());
        }
        catch (Exception exception)
        {
            return await TerminateAsync(run, actor, CustomLoopRunStatus.Failed, "deterministic_exit_audit_failed", $"The deterministic Exit audit could not be recorded: {SafeExceptionClass(exception)}.");
        }

        var publicationBoundary = await RefreshControlUpdateAsync(run);
        if (publicationBoundary.Terminal is not null)
        {
            return publicationBoundary.Terminal;
        }

        run = publicationBoundary.Run!;
        var published = run.Status == CustomLoopRunStatus.CancelRequested
            ? new RunAdvance(run, null)
            : await PublishIfSelectedAsync(run, outputPolicy, iterationResult, "exit", isExit: true, actor);
        if (published.Terminal is not null)
        {
            return published.Terminal;
        }

        run = published.Run!;
        var checkpoint = run.Checkpoint with { PendingExitDecision = false };
        var committed = await CommitCheckpointAsync(run, checkpoint, detail);
        if (committed.Terminal is not null)
        {
            return committed.Terminal;
        }

        var completionBoundary = await ObserveControlBoundaryAsync(committed.Run!, actor);
        return completionBoundary.Terminal ?? await TerminateAsync(completionBoundary.Run!, actor, CustomLoopRunStatus.Completed, null, detail, iterationResult.Content);
    }

    private async Task<RunAdvance> PublishIfSelectedAsync(CustomLoopRunRecord run, CustomLoopContextOutputPolicy policy, CustomLoopRetainedOutput output, string stepId, bool isExit, string actor)
    {
        if (!policy.PublishToInvokingConversation)
        {
            return new RunAdvance(run, null);
        }

        var operationId = PublicationOperationId(run.Id, run.Checkpoint.Iteration, stepId, isExit);
        var conversation = run.InvokingConversation;
        if (conversation is null)
        {
            var omitted = Event(run, Now(run), CustomLoopRunEventKind.ConversationPublished, "Conversation publication was selected but omitted because admission bound no invoking conversation.", run.Checkpoint.Iteration, stepId, published: false, publicationId: operationId);
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

        var intent = Event(run, Now(run), CustomLoopRunEventKind.ConversationPublicationStarted, "Conversation publication intent committed before the idempotent append.", run.Checkpoint.Iteration, stepId, publicationId: operationId);
        var intentPersisted = await PersistAsync(run, Append(run, intent.TimestampUtc, [intent]), IntegrityToken(), outcomeMayExist: false);
        if (intentPersisted.Terminal is not null)
        {
            return intentPersisted;
        }

        run = intentPersisted.Run!;
        CustomLoopConversationPublicationResult publication;
        var publicationDispatched = false;
        using var publicationToken = new CancellationTokenSource(IntegrityWriteTimeout);
        if (!_activeAttemptCancellations.TryAdd(run.Id, publicationToken))
        {
            var terminal = await TerminateAsync(run, actor, CustomLoopRunStatus.Failed, "publication_registration_failed", "Conversation publication could not be registered with the active cancellation protocol, so no append was attempted.");
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

            var request = new CustomLoopConversationPublicationRequest(operationId, run.Id, run.LoopId, run.Checkpoint.Iteration, stepId, conversation.ConversationId, conversation.CapturedVersion, output.Content, output.ContentHash, priorPublications);
            publicationToken.Token.ThrowIfCancellationRequested();
            publicationDispatched = true;
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
            _activeAttemptCancellations.TryRemove(run.Id, out _);
        }

        publication ??= new CustomLoopConversationPublicationResult(CustomLoopConversationPublicationOutcome.Uncertain, null, "Publisher returned no result after publication may have occurred.");

        var isPublished = publication.Outcome is CustomLoopConversationPublicationOutcome.Published or CustomLoopConversationPublicationOutcome.AlreadyPublished;
        var publicationId = publication.PublicationId ?? operationId;
        var eventDetail = publication.Outcome switch
        {
            CustomLoopConversationPublicationOutcome.Published => "Canonical output was published to the invoking conversation.",
            CustomLoopConversationPublicationOutcome.AlreadyPublished => "Idempotent conversation publication was already committed.",
            CustomLoopConversationPublicationOutcome.DefinitelyFailed => "Conversation publication definitely failed; no success is reported.",
            CustomLoopConversationPublicationOutcome.Uncertain => "Conversation publication outcome is uncertain and requires review.",
            _ => "Conversation publisher returned an unsupported outcome that requires review."
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

    private async Task<RunAdvance> RecordAttemptFailureAsync(CustomLoopRunRecord run, string actor, string stepId, int iteration, string correlation, CustomLoopContextAssembly assembly, Exception exception, bool isExit)
    {
        var refreshed = await RefreshControlUpdateAsync(run);
        if (refreshed.Terminal is not null)
        {
            return refreshed;
        }

        run = refreshed.Run!;
        var uncertain = IsUncertainProviderFailure(exception);
        var detail = uncertain ? "Provider attempt was cancelled after dispatch and its outcome cannot be proven." : $"Provider attempt failed without an automatic retry: {SafeExceptionClass(exception)}.";
        var failure = Event(run, Now(run), CustomLoopRunEventKind.NodeAttemptFailed, detail, iteration, stepId, 1, provider: run.ModelSnapshot.Provider, model: run.ModelSnapshot.Model, providerResponseId: correlation);
        var persisted = await PersistAsync(run, Append(run, failure.TimestampUtc, [failure]), IntegrityToken(), outcomeMayExist: uncertain);
        if (persisted.Terminal is not null)
        {
            return persisted;
        }

        run = persisted.Run!;
        try
        {
            var action = isExit ? AuditSchema.Actions.LoopExitDecision : AuditSchema.Actions.LoopNodeAttempt;
            var outcome = uncertain || isExit ? AuditSchema.Outcomes.NeedsReview : AuditSchema.Outcomes.Failed;
            await _auditLog.AppendAsync(AttemptAudit(actor, run, stepId, iteration, correlation, assembly, action, outcome, null, null), IntegrityToken());
        }
        catch (Exception auditException)
        {
            detail = $"Provider failure evidence exists, but its outcome audit failed: {SafeExceptionClass(auditException)}.";
            uncertain = true;
        }

        var status = isExit || uncertain ? CustomLoopRunStatus.NeedsReview : CustomLoopRunStatus.Failed;
        var code = isExit ? "exit_attempt_failed" : uncertain ? "inference_attempt_uncertain" : "inference_attempt_failed";
        var terminal = await TerminateAsync(run, actor, status, code, detail);
        return new RunAdvance(terminal.Run, terminal);
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
        const string failureCode = "post_outcome_persistence_conflict";
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
                    FailureCode = failureCode,
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
        CustomLoopExitDecision? exitDecision = null)
    {
        var metadata = RunMetadata(run);
        metadata["iteration"] = iteration;
        metadata["stepId"] = stepId;
        metadata["attempt"] = 1;
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
            && item.Attempt == 1)?.ToolAuthority;
        metadata["admittedCommands"] = authority is null ? null : string.Join(',', authority.AdmittedMaximum.OrderBy(value => value));
        metadata["currentRoleCommands"] = authority is null ? null : string.Join(',', authority.CurrentRoleCeiling.OrderBy(value => value));
        metadata["effectiveCommands"] = authority is null ? null : string.Join(',', authority.EffectiveAssignments.OrderBy(value => value));
        metadata["roleCeilingHash"] = authority?.RoleCeilingHash;
        metadata["catalogHash"] = authority?.CatalogHash;
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

    private static string? ValidateProviderResult(CustomLoopRunRecord run, CustomLoopInferenceAttemptResult result)
    {
        if (result is null)
        {
            return "The provider executor returned no result after dispatch.";
        }

        if (!string.Equals(result.Provider, run.ModelSnapshot.Provider, StringComparison.Ordinal) || !string.Equals(result.Model, run.ModelSnapshot.Model, StringComparison.Ordinal))
        {
            return "The provider/model result does not match the immutable admitted model snapshot.";
        }

        if (result.ToolRequestsConsumed < 0 || result.ToolRequestsConsumed > CustomLoopLimits.MaxRecordedGovernedToolRequestsPerAttempt || run.Checkpoint.ToolRequestsUsed + result.ToolRequestsConsumed > CustomLoopLimits.MaxRecordedGovernedToolRequestsPerRun)
        {
            return "The provider result reported a governed tool-call count outside the admitted run budget.";
        }

        return null;
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

    private static bool AssignmentSetsEqual(IReadOnlyList<CustomLoopToolAssignment> left, IReadOnlyList<CustomLoopToolAssignment> right)
    {
        return left.Count == right.Count && left.OrderBy(value => value).SequenceEqual(right.OrderBy(value => value));
    }

    private static bool AttemptConsumesBudget(CustomLoopRunRecord run, CustomLoopRunEvent start)
    {
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

    private static bool HasTraceCapacityForDispatch(CustomLoopRunRecord candidate)
    {
        var candidateBytes = JsonSerializer.SerializeToUtf8Bytes(candidate, TraceSizingJsonOptions).LongLength;
        var requiredReserve = CustomLoopLimits.MaxAttemptEvidenceReservationUtf8Bytes + CustomLoopLimits.MaxTraceControlReserveUtf8Bytes + CustomLoopLimits.MaxPermanentTerminalIntegrityReserveUtf8Bytes;
        return candidateBytes + requiredReserve <= CustomLoopLimits.MaxRunTraceUtf8Bytes;
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
        var token = output.Trim();
        if (string.Equals(token, "Complete", StringComparison.Ordinal))
        {
            return CustomLoopExitDecision.Complete;
        }

        return string.Equals(token, "Repeat", StringComparison.Ordinal) ? CustomLoopExitDecision.Repeat : CustomLoopExitDecision.Invalid;
    }

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
        return new CancellationTokenSource(IntegrityWriteTimeout).Token;
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

    private static CustomLoopOrderedRunResult Result(CustomLoopOrderedRunStatus status, CustomLoopRunRecord? run, string detail)
    {
        return new CustomLoopOrderedRunResult(status, run, detail);
    }

    private sealed record RunAdvance(CustomLoopRunRecord? Run, CustomLoopOrderedRunResult? Terminal);

    private sealed record CanonicalOutput(string Text, int OriginalCharacterCount, bool Truncated);

}
