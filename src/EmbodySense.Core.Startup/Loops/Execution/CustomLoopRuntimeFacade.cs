using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Startup.Loops;
using System.Text;

namespace EmbodySense.Core.Startup.Loops.Execution;

internal sealed class CustomLoopRuntimeFacade : IAsyncDisposable
{
    private static readonly TimeSpan IntegrityWriteTimeout = TimeSpan.FromSeconds(30);
    private readonly ICustomLoopDefinitionStore _definitionStore;
    private readonly ICustomLoopRunStore _runStore;
    private readonly ICustomLoopInvocationOperationStore _invocationOperationStore;
    private readonly ICustomLoopWorkspaceExecutionGate _executionGate;
    private readonly CustomLoopAdmissionService _admissionService;
    private readonly CustomLoopRecoveryService _recoveryService;
    private readonly CustomLoopLifecycleService _lifecycleService;
    private readonly CustomLoopOrderedRunner _runner;
    private readonly CustomLoopRuntimeContext _runtimeContext;
    private readonly string _surface;
    private readonly string _actor;
    private readonly string _currentRoleId;
    private readonly CustomLoopModelSnapshot _modelSnapshot;
    private readonly TimeProvider _timeProvider;

    public CustomLoopRuntimeFacade(
        ICustomLoopDefinitionStore definitionStore,
        ICustomLoopRunStore runStore,
        ICustomLoopInvocationOperationStore invocationOperationStore,
        ICustomLoopWorkspaceExecutionGate executionGate,
        CustomLoopAdmissionService admissionService,
        CustomLoopRecoveryService recoveryService,
        CustomLoopLifecycleService lifecycleService,
        CustomLoopOrderedRunner runner,
        CustomLoopRuntimeContext runtimeContext,
        string surface,
        string actor,
        string currentRoleId,
        CustomLoopModelSnapshot modelSnapshot,
        TimeProvider? timeProvider = null)
    {
        _definitionStore = definitionStore ?? throw new ArgumentNullException(nameof(definitionStore));
        _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
        _invocationOperationStore = invocationOperationStore ?? throw new ArgumentNullException(nameof(invocationOperationStore));
        _executionGate = executionGate ?? throw new ArgumentNullException(nameof(executionGate));
        _admissionService = admissionService ?? throw new ArgumentNullException(nameof(admissionService));
        _recoveryService = recoveryService ?? throw new ArgumentNullException(nameof(recoveryService));
        _lifecycleService = lifecycleService ?? throw new ArgumentNullException(nameof(lifecycleService));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _runtimeContext = runtimeContext ?? throw new ArgumentNullException(nameof(runtimeContext));
        _surface = string.IsNullOrWhiteSpace(surface) ? throw new ArgumentException("Surface is required.", nameof(surface)) : surface;
        _actor = string.IsNullOrWhiteSpace(actor) ? throw new ArgumentException("Actor is required.", nameof(actor)) : actor;
        _currentRoleId = string.IsNullOrWhiteSpace(currentRoleId) ? throw new ArgumentException("Current role is required.", nameof(currentRoleId)) : currentRoleId;
        _modelSnapshot = modelSnapshot ?? throw new ArgumentNullException(nameof(modelSnapshot));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<LoopRunInvocationResponse> InvokeAsync(LoopRunInvocationInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        CustomLoopInvocationOperation pending;
        try
        {
            pending = CreatePendingOperation(input);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Invalid(exception.Message);
        }

        CustomLoopInvocationOperation? existingOperation;
        try
        {
            existingOperation = await _invocationOperationStore.GetAsync(input.OperationId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Invalid($"The invocation receipt could not be read safely: {exception.GetType().Name}.");
        }

        if (existingOperation is not null)
        {
            if (!string.Equals(existingOperation.RequestHash, pending.RequestHash, StringComparison.Ordinal))
            {
                return Conflict("The invocation operation id is already bound to different canonical authorized request content.");
            }

            if (existingOperation.State == CustomLoopInvocationOperationState.Complete)
            {
                return await ReplayOperationAsync(existingOperation, cancellationToken);
            }
        }

        CustomLoopExecutionLeaseResult ownership;
        while (true)
        {
            ownership = _executionGate.TryAcquire(input.OperationId, pending.RequestHash);
            if (ownership.Status == CustomLoopExecutionLeaseStatus.WorkspaceHostUnavailable)
            {
                return new LoopRunInvocationResponse("WorkspaceHostUnavailable", null, false, null, [], ownership.Detail);
            }

            if (ownership.Status != CustomLoopExecutionLeaseStatus.WorkspaceBusy)
            {
                break;
            }

            var recoveredAdmission = existingOperation is null ? null : await ReconcilePendingAdmissionBeforeBusyAsync(existingOperation, cancellationToken);
            if (recoveredAdmission is not null)
            {
                return recoveredAdmission;
            }

            var busyReservation = _executionGate.TryReserveWorkspaceBusyOutcome(input.OperationId, pending.RequestHash);
            if (busyReservation.Status == CustomLoopExecutionLeaseStatus.WorkspaceAvailable)
            {
                continue;
            }

            if (busyReservation.Status == CustomLoopExecutionLeaseStatus.OperationInProgress)
            {
                return new LoopRunInvocationResponse("OperationInProgress", null, false, null, [], "The same custom-loop invocation acquired execution ownership or is finalizing its durable busy receipt; retry later.");
            }

            if (busyReservation.Status == CustomLoopExecutionLeaseStatus.OperationConflict || busyReservation.Lease is null)
            {
                return Conflict("The active or reserved invocation operation id is bound to different canonical authorized request content.");
            }

            using (busyReservation.Lease)
            {
                return await RecordWorkspaceBusyAsync(existingOperation ?? pending, cancellationToken);
            }
        }

        if (ownership.Status == CustomLoopExecutionLeaseStatus.OperationInProgress)
        {
            return new LoopRunInvocationResponse("OperationInProgress", null, false, null, [], "The same custom-loop invocation is already executing; retry its durable receipt later.");
        }

        if (ownership.Status == CustomLoopExecutionLeaseStatus.OperationConflict || ownership.Lease is null)
        {
            return Conflict("The active invocation operation id is bound to different canonical authorized request content.");
        }

        using (ownership.Lease)
        {
            CustomLoopInvocationOperation operation;
            try
            {
                var begun = await _invocationOperationStore.BeginAsync(pending, cancellationToken);
                if (begun.Status == CustomLoopInvocationOperationStoreStatus.Conflict)
                {
                    return Conflict("The invocation operation id is already bound to different canonical authorized request content.");
                }

                operation = begun.Operation ?? pending;
                if (operation.State == CustomLoopInvocationOperationState.Complete)
                {
                    return await ReplayOperationAsync(operation, cancellationToken);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return Invalid($"The invocation receipt could not be started safely: {exception.GetType().Name}.");
            }

            CustomLoopRunRecord? admittedByInterruptedOwner;
            try
            {
                admittedByInterruptedOwner = await _runStore.GetByAdmissionOperationAsync(input.OperationId, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return Invalid($"The invocation admission state could not be reconciled safely: {exception.GetType().Name}.");
            }

            CustomLoopContextSnapshot contextSnapshot;
            CustomLoopConversationReference? conversationReference;
            if (admittedByInterruptedOwner is not null)
            {
                contextSnapshot = admittedByInterruptedOwner.ContextSnapshot;
                conversationReference = admittedByInterruptedOwner.InvokingConversation;
            }
            else
            {
                CustomLoopDefinition? definition;
                try
                {
                    definition = await _definitionStore.GetAsync(input.LoopId, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    return await CompleteRejectedAsync(operation, CustomLoopAdmissionStatus.Invalid.ToString(), null, $"The loop definition could not be read safely: {exception.GetType().Name}.");
                }

                if (definition is null)
                {
                    return await CompleteRejectedAsync(operation, CustomLoopAdmissionStatus.NotFound.ToString(), null, "The custom-loop definition does not exist.");
                }

                var capture = await _runtimeContext.CaptureAsync(definition.TriggerPolicy.IncludeInvokingConversation, cancellationToken);
                contextSnapshot = capture.Snapshot;
                conversationReference = capture.ConversationReference;
            }

            var request = new CustomLoopAdmissionRequest(
                input.LoopId,
                input.ExpectedDefinitionVersion,
                input.ExpectedDefinitionHash,
                input.OperationId,
                _actor,
                _surface,
                _currentRoleId,
                input.InvocationPrompt,
                _modelSnapshot,
                conversationReference,
                contextSnapshot);
            var admission = await _admissionService.AdmitAsync(request, cancellationToken);
            if (!admission.IsAdmitted)
            {
                return await CompleteRejectedAsync(operation, admission.Status.ToString(), admission.Run, admission.Detail, admission.ValidationErrors);
            }

            var completed = await CompleteOperationAsync(operation, CustomLoopInvocationOutcome.Admitted, CustomLoopAdmissionStatus.Admitted.ToString(), admission.Run, admission.Detail);
            if (!completed)
            {
                var parked = await ParkUndispatchedAdmissionAsync(admission.Run!);
                return new LoopRunInvocationResponse(CustomLoopAdmissionStatus.AuditUnavailable.ToString(), parked.Status.ToString(), false, Map(parked), admission.ValidationErrors.Select(Map).ToArray(), "The run was admitted, but its strict invocation receipt could not be completed. The undispatched run was conservatively parked and no provider request was dispatched.");
            }

            if (admission.Status == CustomLoopAdmissionStatus.Replayed)
            {
                return new LoopRunInvocationResponse(CustomLoopAdmissionStatus.Admitted.ToString(), admission.Run?.Status.ToString(), false, admission.Run is null ? null : Map(admission.Run), admission.ValidationErrors.Select(Map).ToArray(), "The durable admitted invocation outcome was recovered without another provider dispatch.");
            }

            var execution = await _runner.RunAsync(new CustomLoopOrderedRunRequest(admission.Run!.Id, _actor), cancellationToken);
            var executedRun = execution.Run ?? admission.Run;
            return new LoopRunInvocationResponse(
                CustomLoopAdmissionStatus.Admitted.ToString(),
                execution.Status.ToString(),
                execution.ProviderWasInvoked,
                Map(executedRun),
                admission.ValidationErrors.Select(Map).ToArray(),
                execution.Detail);
        }
    }

    public async Task<LoopRunSnapshot?> GetAsync(string runId, CancellationToken cancellationToken)
    {
        var run = await _runStore.GetAsync(runId, cancellationToken);
        return run is null ? null : Map(run);
    }

    public async Task<IReadOnlyList<LoopRunSummarySnapshot>> ListRecentAsync(int maximumCount, CancellationToken cancellationToken)
    {
        var summaries = await _runStore.ListRecentAsync(maximumCount, cancellationToken);
        return summaries.Select(Map).ToArray();
    }

    public Task<LoopRunControlResponse> PauseAsync(LoopRunControlInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        return ExecuteControlAsync(awaitable: _lifecycleService.PauseAsync(new CustomLoopPauseRequest(input.RunId, input.ExpectedLifecycleVersion, input.OperationId, _actor), cancellationToken));
    }

    public Task<LoopRunControlResponse> CancelAsync(LoopRunControlInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        return ExecuteControlAsync(awaitable: _lifecycleService.CancelAsync(new CustomLoopCancelRequest(input.RunId, input.ExpectedLifecycleVersion, input.OperationId, _actor), cancellationToken));
    }

    public Task<LoopRunControlResponse> ResumeAsync(LoopRunControlInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        return ExecuteControlAsync(awaitable: _lifecycleService.ResumeAsync(new CustomLoopResumeRequest(input.RunId, input.ExpectedLifecycleVersion, input.OperationId, _actor), cancellationToken));
    }

    public async ValueTask DisposeAsync()
    {
        await _executionGate.DisposeAsync();
    }

    private static async Task<LoopRunControlResponse> ExecuteControlAsync(Task<CustomLoopControlResult> awaitable)
    {
        var result = await awaitable;
        return new LoopRunControlResponse(result.Status.ToString(), result.Run is null ? null : Map(result.Run), result.OperationId, result.Detail);
    }

    private CustomLoopInvocationOperation CreatePendingOperation(LoopRunInvocationInput input)
    {
        CustomLoopArtifactIdentifier.Require(input.OperationId, nameof(input.OperationId), CustomLoopLimits.MaxMutationOperationIdCharacters);
        CustomLoopArtifactIdentifier.Require(input.LoopId, nameof(input.LoopId));
        if (input.ExpectedDefinitionVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(input.ExpectedDefinitionVersion), "Expected definition version must be at least one.");
        }

        if (!IsHash(input.ExpectedDefinitionHash))
        {
            throw new ArgumentException("Expected definition hash must be lowercase SHA-256 hexadecimal.", nameof(input.ExpectedDefinitionHash));
        }

        var invocationPrompt = input.InvocationPrompt?.Normalize(NormalizationForm.FormC) ?? string.Empty;
        if (invocationPrompt.Length > CustomLoopLimits.MaxPresetPromptCharacters)
        {
            throw new ArgumentException($"Invocation prompt cannot exceed {CustomLoopLimits.MaxPresetPromptCharacters} characters.", nameof(input.InvocationPrompt));
        }

        var requestHash = CustomLoopInvocationRequestHash.Compute(
            input.OperationId,
            input.LoopId,
            input.ExpectedDefinitionVersion,
            input.ExpectedDefinitionHash,
            _actor,
            _surface,
            _currentRoleId,
            invocationPrompt,
            _modelSnapshot.Provider,
            _modelSnapshot.Model);
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        return new CustomLoopInvocationOperation(
            CustomLoopInvocationOperation.CurrentSchemaVersion,
            input.OperationId,
            requestHash,
            input.LoopId,
            input.ExpectedDefinitionVersion,
            input.ExpectedDefinitionHash,
            _actor,
            _surface,
            _currentRoleId,
            CustomLoopInvocationRequestHash.ComputePromptHash(invocationPrompt),
            _modelSnapshot.Provider,
            _modelSnapshot.Model,
            now,
            now,
            CustomLoopInvocationOperationState.Pending,
            CustomLoopInvocationOutcome.Unknown,
            string.Empty,
            null,
            "The canonical custom-loop invocation is durably pending before context capture or admission.");
    }

    private async Task<LoopRunInvocationResponse> RecordWorkspaceBusyAsync(CustomLoopInvocationOperation operation, CancellationToken cancellationToken)
    {
        try
        {
            var begun = await _invocationOperationStore.BeginAsync(operation, cancellationToken);
            if (begun.Status == CustomLoopInvocationOperationStoreStatus.Conflict)
            {
                return Conflict("The invocation operation id is already bound to different canonical authorized request content.");
            }

            var durable = begun.Operation ?? operation;
            if (durable.State == CustomLoopInvocationOperationState.Complete)
            {
                return await ReplayOperationAsync(durable, cancellationToken);
            }

            var completed = durable with
            {
                UpdatedAtUtc = UtcNow(durable.UpdatedAtUtc),
                State = CustomLoopInvocationOperationState.Complete,
                Outcome = CustomLoopInvocationOutcome.WorkspaceExecutionBusy,
                AdmissionStatus = "WorkspaceExecutionBusy",
                RunId = null,
                Detail = "workspace_execution_busy: another custom-loop run is actively executing; no run, deadline, context snapshot, or provider request was created."
            };
            var stored = await CompleteReceiptAsync(completed);
            if (stored)
            {
                return Busy(completed.Detail);
            }

            var reconciled = await _invocationOperationStore.GetAsync(operation.OperationId, cancellationToken);
            return reconciled is { State: CustomLoopInvocationOperationState.Complete }
                ? await ReplayOperationAsync(reconciled, cancellationToken)
                : Invalid("The workspace busy outcome could not be persisted safely; no provider request was dispatched.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Invalid($"The workspace busy receipt could not be persisted safely: {exception.GetType().Name}; no provider request was dispatched.");
        }
    }

    private async Task<LoopRunInvocationResponse?> ReconcilePendingAdmissionBeforeBusyAsync(CustomLoopInvocationOperation operation, CancellationToken cancellationToken)
    {
        CustomLoopRunRecord? run;
        try
        {
            run = await _runStore.GetByAdmissionOperationAsync(operation.OperationId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Invalid($"The pending invocation receipt could not reconcile its prior admission safely: {exception.GetType().Name}; no provider request was dispatched.");
        }

        if (run is null)
        {
            return null;
        }

        if (!InvocationMatchesRun(operation, run))
        {
            return Conflict("The pending invocation receipt and its prior run do not describe the same canonical authorized invocation.");
        }

        var admissionComplete = CustomLoopRunValidator.HasCompleteAdmissionAudit(run);
        var outcome = admissionComplete ? CustomLoopInvocationOutcome.Admitted : CustomLoopInvocationOutcome.Rejected;
        var admissionStatus = admissionComplete ? CustomLoopAdmissionStatus.Admitted.ToString() : CustomLoopAdmissionStatus.AuditUnavailable.ToString();
        var detail = admissionComplete
            ? "The pending invocation receipt was reconciled to its already admitted run before evaluating a newer workspace-busy owner; no provider request was dispatched."
            : "The pending invocation receipt found an integrity-incomplete prior admission; it was not overwritten by a newer workspace-busy outcome and no provider request was dispatched.";
        var completed = await CompleteOperationAsync(operation, outcome, admissionStatus, run, detail);
        return completed
            ? new LoopRunInvocationResponse(admissionStatus, run.Status.ToString(), false, Map(run), [], detail)
            : new LoopRunInvocationResponse(CustomLoopAdmissionStatus.AuditUnavailable.ToString(), run.Status.ToString(), false, Map(run), [], "The prior admission was found, but its invocation receipt could not be reconciled safely; no provider request was dispatched.");
    }

    private async Task<CustomLoopRunRecord> ParkUndispatchedAdmissionAsync(CustomLoopRunRecord run)
    {
        using var integrity = new CancellationTokenSource(IntegrityWriteTimeout);
        try
        {
            var recovery = await _recoveryService.RecoverAsync(_actor, integrity.Token);
            return recovery.SingleOrDefault(result => string.Equals(result.Run.Id, run.Id, StringComparison.Ordinal))?.Run
                ?? await _runStore.GetAsync(run.Id, integrity.Token)
                ?? run;
        }
        catch
        {
            return await TryReloadRunAsync(run.Id) ?? run;
        }
    }

    private async Task<CustomLoopRunRecord?> TryReloadRunAsync(string runId)
    {
        try
        {
            using var integrity = new CancellationTokenSource(IntegrityWriteTimeout);
            return await _runStore.GetAsync(runId, integrity.Token);
        }
        catch
        {
            return null;
        }
    }

    private static bool InvocationMatchesRun(CustomLoopInvocationOperation operation, CustomLoopRunRecord run)
    {
        var promptMatches = run.AdmittedDefinition.TriggerPolicy.PromptSource switch
        {
            CustomLoopTriggerPromptSource.Invocation => string.Equals(operation.InvocationPromptHash, CustomLoopInvocationRequestHash.ComputePromptHash(run.TriggerPrompt), StringComparison.Ordinal),
            CustomLoopTriggerPromptSource.Preset => string.Equals(run.AdmittedDefinition.TriggerPolicy.PresetPrompt, run.TriggerPrompt, StringComparison.Ordinal),
            CustomLoopTriggerPromptSource.None => run.TriggerPrompt.Length == 0,
            _ => false
        };
        return promptMatches
            && string.Equals(operation.OperationId, run.AdmissionOperationId, StringComparison.Ordinal)
            && string.Equals(operation.LoopId, run.LoopId, StringComparison.Ordinal)
            && operation.ExpectedDefinitionVersion == run.AdmittedDefinition.DefinitionVersion
            && string.Equals(operation.ExpectedDefinitionHash, run.AdmittedDefinition.ContentHash, StringComparison.Ordinal)
            && string.Equals(operation.Surface, run.Surface, StringComparison.Ordinal)
            && string.Equals(operation.CurrentRoleId, run.AdmittedDefinition.RoleId, StringComparison.Ordinal)
            && string.Equals(operation.Provider, run.ModelSnapshot.Provider, StringComparison.Ordinal)
            && string.Equals(operation.Model, run.ModelSnapshot.Model, StringComparison.Ordinal);
    }

    private async Task<LoopRunInvocationResponse> CompleteRejectedAsync(
        CustomLoopInvocationOperation operation,
        string admissionStatus,
        CustomLoopRunRecord? run,
        string detail,
        IReadOnlyList<CustomLoopValidationError>? validationErrors = null)
    {
        var completed = await CompleteOperationAsync(operation, CustomLoopInvocationOutcome.Rejected, admissionStatus, run, detail);
        return completed
            ? new LoopRunInvocationResponse(admissionStatus, null, false, run is null ? null : Map(run), (validationErrors ?? []).Select(Map).ToArray(), detail)
            : new LoopRunInvocationResponse(CustomLoopAdmissionStatus.AuditUnavailable.ToString(), null, false, run is null ? null : Map(run), (validationErrors ?? []).Select(Map).ToArray(), "The invocation was rejected, but its strict operation receipt could not be completed safely; no provider request was dispatched.");
    }

    private async Task<bool> CompleteOperationAsync(CustomLoopInvocationOperation operation, CustomLoopInvocationOutcome outcome, string admissionStatus, CustomLoopRunRecord? run, string detail)
    {
        var completed = operation with
        {
            UpdatedAtUtc = UtcNow(operation.UpdatedAtUtc),
            State = CustomLoopInvocationOperationState.Complete,
            Outcome = outcome,
            AdmissionStatus = admissionStatus,
            RunId = run?.Id,
            Detail = detail
        };
        return await CompleteReceiptAsync(completed);
    }

    private async Task<bool> CompleteReceiptAsync(CustomLoopInvocationOperation completed)
    {
        using var integrity = new CancellationTokenSource(IntegrityWriteTimeout);
        try
        {
            var result = await _invocationOperationStore.CompleteAsync(completed, integrity.Token);
            return result.Status is CustomLoopInvocationOperationStoreStatus.Completed or CustomLoopInvocationOperationStoreStatus.Replayed;
        }
        catch
        {
            return false;
        }
    }

    private async Task<LoopRunInvocationResponse> ReplayOperationAsync(CustomLoopInvocationOperation operation, CancellationToken cancellationToken)
    {
        LoopRunSnapshot? run = null;
        CustomLoopRunRecord? durableRun = null;
        if (operation.RunId is not null)
        {
            try
            {
                durableRun = await _runStore.GetAsync(operation.RunId, cancellationToken);
                run = durableRun is null ? null : Map(durableRun);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return Invalid($"The invocation receipt was found, but its run could not be read safely: {exception.GetType().Name}.");
            }
        }

        if (operation.Outcome == CustomLoopInvocationOutcome.WorkspaceExecutionBusy)
        {
            return Busy("The durable workspace_execution_busy outcome was replayed; no run, context capture, or provider dispatch was attempted.");
        }

        if (operation.Outcome == CustomLoopInvocationOutcome.Admitted && durableRun is null)
        {
            return Invalid("The durable admitted invocation receipt refers to a missing run; no provider request was dispatched.");
        }

        return new LoopRunInvocationResponse(
            operation.AdmissionStatus,
            durableRun?.Status.ToString(),
            false,
            run,
            [],
            operation.Outcome == CustomLoopInvocationOutcome.Admitted
                ? "The durable admitted invocation outcome was replayed without another provider dispatch."
                : $"The durable {operation.AdmissionStatus} invocation outcome was replayed without context capture or provider dispatch.");
    }

    private DateTimeOffset UtcNow(DateTimeOffset minimum)
    {
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        return now < minimum ? minimum : now;
    }

    private static bool IsHash(string? value)
    {
        return value is { Length: CustomLoopLimits.Sha256HexCharacters } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static LoopRunInvocationResponse Busy(string detail)
    {
        return new LoopRunInvocationResponse("WorkspaceExecutionBusy", null, false, null, [], detail);
    }

    private static LoopRunInvocationResponse Conflict(string detail)
    {
        return new LoopRunInvocationResponse(CustomLoopAdmissionStatus.Conflict.ToString(), null, false, null, [], detail);
    }

    private static LoopRunInvocationResponse MapAdmission(CustomLoopAdmissionResult result, string? executionStatus, bool wasDispatched)
    {
        return new LoopRunInvocationResponse(
            result.Status.ToString(),
            executionStatus,
            wasDispatched,
            result.Run is null ? null : Map(result.Run),
            result.ValidationErrors.Select(Map).ToArray(),
            result.Detail);
    }

    private static LoopRunInvocationResponse Invalid(string detail)
    {
        return new LoopRunInvocationResponse(CustomLoopAdmissionStatus.Invalid.ToString(), null, false, null, [], detail);
    }

    private static LoopValidationError Map(CustomLoopValidationError error)
    {
        return new LoopValidationError(error.Code, error.Field, error.Message);
    }

    internal static LoopRunSummarySnapshot Map(CustomLoopRunSummary summary)
    {
        return new LoopRunSummarySnapshot(
            summary.Id,
            summary.LoopId,
            summary.AdmissionOperationId,
            summary.DefinitionVersion,
            summary.Status.ToString(),
            summary.CreatedAtUtc,
            summary.UpdatedAtUtc,
            summary.CompletedAtUtc,
            summary.Iteration,
            summary.NextStepIndex,
            summary.FailureCode,
            summary.IsDeleted);
    }

    internal static LoopRunSnapshot Map(CustomLoopRunRecord run)
    {
        return new LoopRunSnapshot(
            run.SchemaVersion,
            run.Id,
            run.LoopId,
            run.LifecycleVersion,
            run.Status.ToString(),
            run.CreatedAtUtc,
            run.UpdatedAtUtc,
            run.CompletedAtUtc,
            run.Surface,
            new LoopRunModelSnapshot(run.ModelSnapshot.Provider, run.ModelSnapshot.Model),
            run.AdmissionOperationId,
            run.AdmissionActor,
            run.AdmissionRequestHash,
            LoopAuthoringFacade.Map(run.AdmittedDefinition),
            run.TriggerPrompt,
            run.InvokingConversation is null ? null : new LoopRunConversationReference(run.InvokingConversation.ConversationId, run.InvokingConversation.CapturedVersion, run.InvokingConversation.CapturedAtUtc),
            new LoopRunContextSnapshot(
                run.ContextSnapshot.SchemaVersion,
                run.ContextSnapshot.CapturedAtUtc,
                run.ContextSnapshot.ManifestHash,
                run.ContextSnapshot.SourceManifest.Select(Map).ToArray(),
                run.ContextSnapshot.DirectoryRoleMessages.Select(Map).ToArray(),
                run.ContextSnapshot.InvokingConversationMessages.Select(Map).ToArray()),
            new LoopRunExecutionClockSnapshot(run.ExecutionClock.AccumulatedRunningMilliseconds, run.ExecutionClock.ActiveSinceUtc),
            Map(run.Checkpoint),
            run.Events.Select(Map).ToArray(),
            run.FinalOutput,
            run.FailureCode,
            run.FailureDetail);
    }

    private static LoopRunMessageSnapshot Map(CustomLoopMessageSnapshot message)
    {
        return new LoopRunMessageSnapshot(ToRole(message.Role), message.Content);
    }

    private static LoopRunContextManifestSourceSnapshot Map(CustomLoopContextManifestSource source)
    {
        return new LoopRunContextManifestSourceSnapshot(
            source.Order,
            source.SourceType.ToString(),
            source.SourceId,
            source.SourcePath,
            source.Provenance.ToString(),
            source.TrustClass.ToString(),
            ToRole(source.Role),
            source.Content,
            source.ContentHash,
            source.OriginalCharacterCount,
            source.UsedCharacterCount,
            source.Truncated,
            source.TruncationReason,
            source.OmissionReason,
            source.CapturedAtUtc);
    }

    private static LoopRunCheckpointSnapshot Map(CustomLoopRunCheckpoint checkpoint)
    {
        return new LoopRunCheckpointSnapshot(
            checkpoint.Iteration,
            checkpoint.NextStepIndex,
            checkpoint.AcceptedRepeatCount,
            checkpoint.PendingExitDecision,
            checkpoint.EarlierRetainedOutputs.Select(Map).ToArray(),
            checkpoint.PreviousIterationResult is null ? null : Map(checkpoint.PreviousIterationResult),
            checkpoint.CurrentIterationResult is null ? null : Map(checkpoint.CurrentIterationResult),
            checkpoint.ToolRequestsUsed,
            checkpoint.LastCommittedSequence);
    }

    private static LoopRunRetainedOutputSnapshot Map(CustomLoopRetainedOutput output)
    {
        return new LoopRunRetainedOutputSnapshot(output.StepId, output.Iteration, output.Content, output.ContentHash);
    }

    private static LoopRunEventSnapshot Map(CustomLoopRunEvent runEvent)
    {
        return new LoopRunEventSnapshot(
            runEvent.Sequence,
            runEvent.EventId,
            runEvent.TimestampUtc,
            runEvent.Kind.ToString(),
            runEvent.Iteration,
            runEvent.StepId,
            runEvent.Attempt,
            runEvent.Detail,
            runEvent.ContextBlocks.Select(Map).ToArray(),
            runEvent.CanonicalOutput,
            runEvent.OriginalOutputCharacterCount,
            runEvent.CanonicalOutputTruncated,
            runEvent.RetainedForLoopReasoning,
            runEvent.PublishedToInvokingConversation,
            runEvent.ConversationPublicationId,
            runEvent.Provider,
            runEvent.Model,
            runEvent.ProviderResponseId,
            runEvent.ExitDecision?.ToString(),
            runEvent.ToolAuthority is null ? null : Map(runEvent.ToolAuthority),
            runEvent.ToolEvidence is null ? null : Map(runEvent.ToolEvidence));
    }

    private static LoopRunToolAuthoritySnapshot Map(CustomLoopToolAuthoritySnapshot authority)
    {
        return new LoopRunToolAuthoritySnapshot(
            authority.RoleId,
            authority.AdmittedMaximum.Select(value => value.ToString()).ToArray(),
            authority.CurrentRoleCeiling.Select(value => value.ToString()).ToArray(),
            authority.ImplementedCatalog.Select(value => value.ToString()).ToArray(),
            authority.EffectiveAssignments.Select(value => value.ToString()).ToArray(),
            authority.RoleCeilingHash,
            authority.CatalogHash,
            authority.EvaluatedAtUtc,
            authority.IsValid,
            authority.Detail);
    }

    private static LoopRunToolEvidenceSnapshot Map(CustomLoopToolTraceEvidence evidence)
    {
        return new LoopRunToolEvidenceSnapshot(
            evidence.Phase.ToString(),
            evidence.RequestOrdinal,
            evidence.RequestCorrelationId,
            evidence.BrokerRequestId,
            evidence.Command.ToString(),
            evidence.TargetPath,
            evidence.Content,
            evidence.Pattern,
            evidence.ResolvedTarget,
            Map(evidence.Authority),
            evidence.Governance is null ? null : new LoopRunToolGovernanceSnapshot(
                evidence.Governance.AuthorityDecision.ToString(),
                evidence.Governance.AuthorityDetail,
                evidence.Governance.PermissionDecision?.ToString(),
                evidence.Governance.PermissionMatchedPath,
                evidence.Governance.PermissionDetail,
                evidence.Governance.PermissionPolicyHash,
                evidence.Governance.ApprovalDecision.ToString(),
                evidence.Governance.ApprovalDecisionBy,
                evidence.Governance.ApprovalDetail),
            evidence.Outcome?.ToString(),
            evidence.CanonicalResultReturnedToModel,
            evidence.CanonicalResultHash,
            evidence.CanonicalResultCharacterCount,
            evidence.ReturnedToModel,
            evidence.ReservedUtf8Bytes);
    }

    private static LoopRunContextBlockSnapshot Map(CustomLoopContextBlock block)
    {
        return new LoopRunContextBlockSnapshot(
            block.Source.ToString(),
            block.SourceId,
            ToRole(block.Role),
            block.Included,
            block.OmissionReason,
            block.Content,
            block.ContentHash,
            block.CharacterCount,
            block.Truncated,
            block.SourceVersion);
    }

    private static string ToRole(LlmMessageRole role) => role.ToString().ToLowerInvariant();
}
