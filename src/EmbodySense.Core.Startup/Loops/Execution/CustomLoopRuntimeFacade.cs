using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Startup.Loops.Execution.Models;
using EmbodySense.Core.Startup.Loops.Models;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.ReceiptRetention.Models;
using EmbodySense.Core.Application.Loops.TraceRetention.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Application.Loops.ReceiptRetention;
using EmbodySense.Core.Application.Loops.TraceRetention;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Startup.Loops;
using System.Text;

namespace EmbodySense.Core.Startup.Loops.Execution;

internal sealed class CustomLoopRuntimeFacade : IAsyncDisposable
{
    private static readonly TimeSpan _integrityWriteTimeout = TimeSpan.FromSeconds(30);
    private static readonly CustomExecutionAvailability _availableCustomExecution = new(true, "Available", "Custom-loop hosting is available and interrupted-run recovery is complete.");
    private readonly ICustomLoopDefinitionStore _definitionStore;
    private readonly ICustomLoopRunStore _runStore;
    private readonly ICustomLoopInvocationOperationStore _invocationOperationStore;
    private readonly CustomLoopInvocationReceiptRetentionService _invocationReceiptRetention;
    private readonly ICustomLoopControlOperationStore _controlOperationStore;
    private readonly ICustomLoopWorkspaceExecutionGate _executionGate;
    private readonly CustomLoopAdmissionService _admissionService;
    private readonly CustomLoopRecoveryService _recoveryService;
    private readonly CustomLoopLifecycleService _lifecycleService;
    private readonly CustomLoopOrderedRunner _runner;
    private readonly CustomLoopRuntimeContext _runtimeContext;
    private readonly SemaphoreSlim _executionAvailabilityGate = new(1, 1);
    private readonly string _surface;
    private readonly string _actor;
    private readonly string _currentRoleId;
    private readonly CustomLoopModelSnapshot _modelSnapshot;
    private readonly TimeProvider _timeProvider;
    private bool _customExecutionAvailable;
    private bool _customExecutionReacquisitionAllowed;
    private bool _customRecoveryRequired;

    /// <summary>
    /// Composes the retained custom-loop host over persistence, admission, lifecycle, execution,
    /// context-capture, and workspace-ownership services.
    /// </summary>
    /// <param name="definitionStore">The definition store.</param>
    /// <param name="runStore">The run store.</param>
    /// <param name="invocationOperationStore">The invocation operation store.</param>
    /// <param name="invocationReceiptRetention">The invocation receipt retention.</param>
    /// <param name="controlOperationStore">The control operation store.</param>
    /// <param name="executionGate">The execution gate.</param>
    /// <param name="admissionService">The admission service.</param>
    /// <param name="recoveryService">The recovery service.</param>
    /// <param name="lifecycleService">The lifecycle service.</param>
    /// <param name="runner">The runner.</param>
    /// <param name="runtimeContext">The runtime context.</param>
    /// <param name="customExecutionAvailable">The custom execution available.</param>
    /// <param name="customExecutionReacquisitionAllowed">The custom execution reacquisition allowed.</param>
    /// <param name="customRecoveryRequired">The custom recovery required.</param>
    /// <param name="surface">The surface.</param>
    /// <param name="actor">The actor.</param>
    /// <param name="currentRoleId">The current role identifier.</param>
    /// <param name="modelSnapshot">The model snapshot.</param>
    /// <param name="timeProvider">The time provider.</param>
    public CustomLoopRuntimeFacade(
        ICustomLoopDefinitionStore definitionStore,
        ICustomLoopRunStore runStore,
        ICustomLoopInvocationOperationStore invocationOperationStore,
        CustomLoopInvocationReceiptRetentionService invocationReceiptRetention,
        ICustomLoopControlOperationStore controlOperationStore,
        ICustomLoopWorkspaceExecutionGate executionGate,
        CustomLoopAdmissionService admissionService,
        CustomLoopRecoveryService recoveryService,
        CustomLoopLifecycleService lifecycleService,
        CustomLoopOrderedRunner runner,
        CustomLoopRuntimeContext runtimeContext,
        bool customExecutionAvailable,
        bool customExecutionReacquisitionAllowed,
        bool customRecoveryRequired,
        string surface,
        string actor,
        string currentRoleId,
        CustomLoopModelSnapshot modelSnapshot,
        TimeProvider? timeProvider = null)
    {
        _definitionStore = definitionStore ?? throw new ArgumentNullException(nameof(definitionStore));
        _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
        _invocationOperationStore = invocationOperationStore ?? throw new ArgumentNullException(nameof(invocationOperationStore));
        _invocationReceiptRetention = invocationReceiptRetention ?? throw new ArgumentNullException(nameof(invocationReceiptRetention));
        _controlOperationStore = controlOperationStore ?? throw new ArgumentNullException(nameof(controlOperationStore));
        _executionGate = executionGate ?? throw new ArgumentNullException(nameof(executionGate));
        _admissionService = admissionService ?? throw new ArgumentNullException(nameof(admissionService));
        _recoveryService = recoveryService ?? throw new ArgumentNullException(nameof(recoveryService));
        _lifecycleService = lifecycleService ?? throw new ArgumentNullException(nameof(lifecycleService));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _runtimeContext = runtimeContext ?? throw new ArgumentNullException(nameof(runtimeContext));
        _customExecutionAvailable = customExecutionAvailable;
        _customExecutionReacquisitionAllowed = customExecutionReacquisitionAllowed;
        _customRecoveryRequired = customRecoveryRequired;
        _surface = string.IsNullOrWhiteSpace(surface) ? throw new ArgumentException("Surface is required.", nameof(surface)) : surface;
        _actor = string.IsNullOrWhiteSpace(actor) ? throw new ArgumentException("Actor is required.", nameof(actor)) : actor;
        _currentRoleId = string.IsNullOrWhiteSpace(currentRoleId) ? throw new ArgumentException("Current role is required.", nameof(currentRoleId)) : currentRoleId;
        _modelSnapshot = modelSnapshot ?? throw new ArgumentNullException(nameof(modelSnapshot));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal bool CustomRecoveryRequired => Volatile.Read(ref _customRecoveryRequired);

    /// <summary>
    /// Reconciles or admits one idempotent custom-loop invocation and, only after durable admission,
    /// executes its ordered run while holding workspace execution ownership.
    /// </summary>
    /// <param name="input">The canonical request identity, expected definition, prompt, and invoking conversation version.</param>
    /// <param name="cancellationToken">The token used to cancel reconciliation, admission, execution, and persistence.</param>
    /// <returns>
    /// A task whose result reports durable admission, execution, validation, conflict, busy-host,
    /// receipt, recovery, and audit-integrity outcomes without making ambiguous failures look successful.
    /// </returns>
    /// <remarks>
    /// Reusing an operation identity with different canonical request content is rejected. Replays
    /// reconcile durable receipts and prior admissions before any provider dispatch. If strict receipt
    /// completion fails after admission, the undispatched run is conservatively parked when that can be
    /// proved. Unsupported run-discovery schemas fail closed with explicit cleanup guidance.
    /// </remarks>
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

        // Receipts are reconciled before availability or lease acquisition so an older durable outcome
        // cannot be overwritten by a newer workspace-busy observation for the same operation identity.
        CustomLoopInvocationOperation? existingOperation;
        try
        {
            existingOperation = await _invocationOperationStore.GetAsync(input.OperationId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ReceiptUnavailable($"The invocation receipt could not be read safely: {exception.GetType().Name}.");
        }

        if (existingOperation is not null)
        {
            if (!string.Equals(existingOperation.RequestHash, pending.RequestHash, StringComparison.Ordinal))
            {
                return Conflict("The invocation operation id is already bound to different canonical authorized request content.");
            }

            var conversationValidation = await ValidateInvocationConversationAsync(existingOperation, cancellationToken);
            if (conversationValidation is not null)
            {
                return conversationValidation;
            }

            if (existingOperation.State == CustomLoopInvocationOperationState.Complete)
            {
                if (CustomRecoveryRequired)
                {
                    var recovery = await EnsureCustomExecutionAvailableAsync(cancellationToken);
                    if (!recovery.Available)
                    {
                        return new LoopRunInvocationResponse(recovery.Status, null, false, null, [], recovery.Detail);
                    }
                }

                return await ReplayOperationAsync(existingOperation, cancellationToken);
            }

            var pendingTerminal = await TryCompletePendingTerminalBindingAsync(existingOperation, cancellationToken);
            if (pendingTerminal is not null)
            {
                return pendingTerminal;
            }
        }

        var availability = await EnsureCustomExecutionAvailableAsync(cancellationToken);
        if (!availability.Available)
        {
            return new LoopRunInvocationResponse(availability.Status, null, false, null, [], availability.Detail);
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

            // Admission can commit before its receipt is finalized. Reconcile that evidence first so
            // the later busy host never replaces an already admitted outcome.
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
                var begun = await WriteReceiptWithRetentionAsync(token => _invocationOperationStore.BeginAsync(pending, token), cancellationToken);
                if (begun.Status == CustomLoopInvocationOperationStoreStatus.Conflict)
                {
                    return Conflict("The invocation operation id is already bound to different canonical authorized request content.");
                }

                if (IsReceiptWriteFailureStatus(begun.Status))
                {
                    return ReceiptWriteFailure(begun.Status);
                }

                operation = begun.Operation ?? pending;
                var conversationValidation = await ValidateInvocationConversationAsync(operation, cancellationToken);
                if (conversationValidation is not null)
                {
                    return conversationValidation;
                }

                if (operation.State == CustomLoopInvocationOperationState.Complete)
                {
                    return await ReplayOperationAsync(operation, cancellationToken);
                }

                var pendingTerminal = await TryCompletePendingTerminalBindingAsync(operation, cancellationToken);
                if (pendingTerminal is not null)
                {
                    return pendingTerminal;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return ReceiptUnavailable($"The invocation receipt could not be started safely: {exception.GetType().Name}.");
            }

            CustomLoopRunRecord? admittedByInterruptedOwner;
            try
            {
                admittedByInterruptedOwner = await _runStore.GetByAdmissionOperationAsync(input.OperationId, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return ReceiptUnavailable($"The invocation admission state could not be reconciled safely: {exception.GetType().Name}.");
            }

            CustomLoopContextSnapshot contextSnapshot;
            CustomLoopConversationReference? conversationReference;
            if (admittedByInterruptedOwner is not null)
            {
                var priorRunBinding = await BindPendingOperationToRunAsync(operation, admittedByInterruptedOwner, cancellationToken);
                if (priorRunBinding.Failure is not null)
                {
                    return priorRunBinding.Failure;
                }

                operation = priorRunBinding.Operation!;
                if (!InvocationMatchesRun(operation, admittedByInterruptedOwner))
                {
                    return Conflict("The pending invocation receipt and its prior run do not describe the same bound conversation and captured context.");
                }

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
                    var detail = $"The loop definition could not be read safely: {exception.GetType().Name}.";
                    if (operation.BindingState == CustomLoopInvocationBindingState.CapturedContext)
                    {
                        operation = operation with { UpdatedAtUtc = UtcNow(operation.UpdatedAtUtc), Detail = detail };
                    }
                    else
                    {
                        var identity = await CaptureConversationIdentityAsync(cancellationToken);
                        if (identity.Failure is not null)
                        {
                            return identity.Failure;
                        }

                        var invalidBinding = await BindOperationAsync(operation, identity.ConversationId!, CustomLoopInvocationBindingState.ConversationInvalid, contextIdentityHash: null, detail, cancellationToken);
                        if (invalidBinding.Failure is not null)
                        {
                            return invalidBinding.Failure;
                        }

                        operation = invalidBinding.Operation!;
                    }

                    return await CompleteRejectedAsync(operation, CustomLoopAdmissionStatus.Invalid.ToString(), null, operation.Detail);
                }

                if (definition is null)
                {
                    const string Detail = "The custom-loop definition does not exist.";
                    var bindingState = operation.BindingState == CustomLoopInvocationBindingState.CapturedContext
                        ? CustomLoopInvocationBindingState.CapturedContextNotFound
                        : CustomLoopInvocationBindingState.ConversationNotFound;
                    var identity = operation.InvokingConversationId is null ? await CaptureConversationIdentityAsync(cancellationToken) : new ConversationIdentityResult(operation.InvokingConversationId, null);
                    if (identity.Failure is not null)
                    {
                        return identity.Failure;
                    }

                    var conversationId = identity.ConversationId!;
                    var conversationBinding = await BindOperationAsync(operation, conversationId, bindingState, operation.ContextIdentityHash, Detail, cancellationToken);
                    if (conversationBinding.Failure is not null)
                    {
                        return conversationBinding.Failure;
                    }

                    operation = conversationBinding.Operation!;
                    return await CompleteRejectedAsync(operation, CustomLoopAdmissionStatus.NotFound.ToString(), null, operation.Detail);
                }

                var capture = await _runtimeContext.CaptureAsync(definition.TriggerPolicy.IncludeInvokingConversation, cancellationToken);
                contextSnapshot = capture.Snapshot;
                conversationReference = capture.ConversationReference;
                var contextBinding = await BindOperationAsync(operation, conversationReference.ConversationId, CustomLoopInvocationBindingState.CapturedContext, CustomLoopContextSnapshotHash.ComputeIdentity(contextSnapshot), detail: null, cancellationToken);
                if (contextBinding.Failure is not null)
                {
                    return contextBinding.Failure;
                }

                operation = contextBinding.Operation!;
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
            CustomLoopAdmissionResult admission;
            try
            {
                admission = await _admissionService.AdmitAsync(request, cancellationToken);
            }
            catch (UnsupportedCustomLoopRunDiscoveryIndexSchemaException exception)
            {
                MarkCustomRecoveryRequired();
                throw new LoopRunEvidenceUnsupportedSchemaException(exception);
            }

            if (!admission.IsAdmitted)
            {
                if (admission.Status == CustomLoopAdmissionStatus.NotFound && operation.BindingState == CustomLoopInvocationBindingState.CapturedContext)
                {
                    var notFoundBinding = await BindOperationAsync(operation, operation.InvokingConversationId!, CustomLoopInvocationBindingState.CapturedContextNotFound, operation.ContextIdentityHash, admission.Detail, cancellationToken);
                    if (notFoundBinding.Failure is not null)
                    {
                        return notFoundBinding.Failure;
                    }

                    operation = notFoundBinding.Operation!;
                }

                return await CompleteRejectedAsync(operation, admission.Status.ToString(), admission.Run, admission.Detail, admission.ValidationErrors);
            }

            var completed = await CompleteOperationAsync(operation, CustomLoopInvocationOutcome.Admitted, CustomLoopAdmissionStatus.Admitted.ToString(), admission.Run, admission.Detail);
            if (!completed)
            {
                var parked = await ParkUndispatchedAdmissionAsync(admission.Run!);
                var detail = parked.IsParked
                    ? "The run was admitted, but its strict invocation receipt could not be completed. The undispatched run was conservatively parked and no provider request was dispatched."
                    : $"The run was admitted, but its strict invocation receipt could not be completed and automatic parking could not be proved: {parked.Detail} No provider request was dispatched by this invocation path.";
                return new LoopRunInvocationResponse(CustomLoopAdmissionStatus.AuditUnavailable.ToString(), parked.Run.Status.ToString(), false, Map(parked.Run), admission.ValidationErrors.Select(Map).ToArray(), detail);
            }

            if (admission.Status == CustomLoopAdmissionStatus.Replayed)
            {
                return new LoopRunInvocationResponse(CustomLoopAdmissionStatus.Admitted.ToString(), admission.Run?.Status.ToString(), false, admission.Run is null ? null : Map(admission.Run), admission.ValidationErrors.Select(Map).ToArray(), "The durable admitted invocation outcome was recovered without another provider dispatch.");
            }

            var execution = await ExecuteOrderedRunAsync(_runner.RunAsync(new CustomLoopOrderedRunRequest(admission.Run!.Id, _actor), cancellationToken));
            CustomLoopRunRecord? executedRun = execution.Run;
            var executionDetail = execution.Detail;
            if (executedRun is null)
            {
                using var integrity = new CancellationTokenSource(_integrityWriteTimeout);
                try
                {
                    executedRun = await _runStore.GetAsync(admission.Run.Id, integrity.Token);
                }
                catch (Exception exception)
                {
                    executionDetail = $"{execution.Detail} The durable post-execution run snapshot could not be refreshed safely: {exception.GetType().Name}.";
                }
            }

            return new LoopRunInvocationResponse(
                CustomLoopAdmissionStatus.Admitted.ToString(),
                execution.Status.ToString(),
                execution.ProviderWasInvoked,
                executedRun is null ? null : Map(executedRun),
                admission.ValidationErrors.Select(Map).ToArray(),
                executionDetail);
        }
    }

    /// <summary>
    /// Reads the full durable projection of one retained run.
    /// </summary>
    /// <param name="runId">The durable run identifier.</param>
    /// <param name="cancellationToken">The token used to cancel the read.</param>
    /// <returns>A task whose result is the run, or null when it is not retained.</returns>
    public async Task<LoopRunSnapshot?> GetAsync(string runId, CancellationToken cancellationToken)
    {
        var run = await _runStore.GetAsync(runId, cancellationToken);
        return run is null ? null : Map(run);
    }

    /// <summary>
    /// Reads the newest retained run summaries across the workspace.
    /// </summary>
    /// <param name="maximumCount">The bounded maximum number of summaries.</param>
    /// <param name="cancellationToken">The token used to cancel the read.</param>
    /// <returns>A task whose result contains the newest retained summaries in store order.</returns>
    public async Task<IReadOnlyList<LoopRunSummarySnapshot>> ListRecentAsync(int maximumCount, CancellationToken cancellationToken)
    {
        var summaries = await ExecuteRunStoreReadAsync(() => _runStore.ListRecentAsync(maximumCount, cancellationToken));
        return summaries.Select(Map).ToArray();
    }

    /// <summary>
    /// Reads one cursor page of retained run summaries, optionally restricted to one loop.
    /// </summary>
    /// <param name="maximumCount">The bounded maximum page size.</param>
    /// <param name="loopId">An optional exact loop identifier filter.</param>
    /// <param name="cursor">An opaque continuation cursor from a prior matching request.</param>
    /// <param name="cancellationToken">The token used to cancel the read.</param>
    /// <returns>A task whose result contains the page and an optional continuation cursor.</returns>
    public async Task<LoopRunSummaryPageSnapshot> ListPageAsync(int maximumCount, string? loopId, string? cursor, CancellationToken cancellationToken)
    {
        var page = await ExecuteRunStoreReadAsync(() => _runStore.ListPageAsync(new CustomLoopRunPageRequest(maximumCount, loopId, cursor), cancellationToken));
        return new LoopRunSummaryPageSnapshot(page.Items.Select(Map).ToArray(), page.ContinuationCursor);
    }

    /// <summary>
    /// Idempotently requests a lifecycle pause at a safe execution boundary.
    /// </summary>
    /// <param name="input">The run, expected lifecycle version, and operation identity.</param>
    /// <param name="cancellationToken">The token used to cancel recovery and lifecycle persistence.</param>
    /// <returns>A task whose result reports the durable control status and resulting run projection when available.</returns>
    public async Task<LoopRunControlResponse> PauseAsync(LoopRunControlInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (CustomRecoveryRequired)
        {
            var recovery = await EnsureCustomExecutionAvailableAsync(cancellationToken);
            if (!recovery.Available)
            {
                return new LoopRunControlResponse(recovery.Status, null, input.OperationId, recovery.Detail);
            }
        }

        await _executionAvailabilityGate.WaitAsync(cancellationToken);
        try
        {
            return await ExecuteControlAsync(awaitable: _lifecycleService.PauseAsync(new CustomLoopPauseRequest(input.RunId, input.ExpectedLifecycleVersion, input.OperationId, _actor), cancellationToken));
        }
        finally
        {
            _executionAvailabilityGate.Release();
        }
    }

    /// <summary>
    /// Idempotently requests cancellation and, when required, reacquires retained runtime ownership
    /// before retrying the exact operation.
    /// </summary>
    /// <param name="input">The run, expected lifecycle version, and operation identity.</param>
    /// <param name="cancellationToken">The token used to cancel receipt reconciliation, recovery, and lifecycle persistence.</param>
    /// <returns>A task whose result reports the durable control status and resulting run projection when available.</returns>
    public async Task<LoopRunControlResponse> CancelAsync(LoopRunControlInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var replay = await TryReplayControlAsync(CustomLoopControlKind.Cancel, input, cancellationToken);
        if (replay is not null)
        {
            return replay;
        }

        if (CustomRecoveryRequired)
        {
            var recovery = await EnsureCustomExecutionAvailableAsync(cancellationToken);
            if (!recovery.Available)
            {
                return new LoopRunControlResponse(recovery.Status, null, input.OperationId, recovery.Detail);
            }
        }

        var request = new CustomLoopCancelRequest(input.RunId, input.ExpectedLifecycleVersion, input.OperationId, _actor);
        var result = await ExecuteControlResultAsync(_lifecycleService.CancelAsync(request, cancellationToken));
        if (!RequiresCancellationOwnerRecovery(result))
        {
            return MapControl(result);
        }

        // A pending cancel receipt can outlive its original host owner. Reacquire retained hosting,
        // then replay the exact operation id instead of creating a second control request.
        var availability = await EnsureCustomExecutionAvailableAsync(cancellationToken);
        if (!availability.Available)
        {
            return MapControl(result with { Detail = $"{result.Detail} Retained-runtime recovery did not acquire hosting: {availability.Detail}" });
        }

        return MapControl(await ExecuteControlResultAsync(_lifecycleService.CancelAsync(request, cancellationToken)));
    }

    /// <summary>
    /// Idempotently resumes a paused run after retained runtime recovery and workspace ownership are available.
    /// </summary>
    /// <param name="input">The run, expected lifecycle version, and operation identity.</param>
    /// <param name="cancellationToken">The token used to cancel receipt reconciliation, recovery, execution, and persistence.</param>
    /// <returns>A task whose result reports the durable control status and resulting run projection when available.</returns>
    public async Task<LoopRunControlResponse> ResumeAsync(LoopRunControlInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var replay = await TryReplayControlAsync(CustomLoopControlKind.Resume, input, cancellationToken);
        if (replay is not null)
        {
            return replay;
        }

        var availability = await EnsureCustomExecutionAvailableAsync(cancellationToken);
        if (!availability.Available)
        {
            return new LoopRunControlResponse(availability.Status, null, input.OperationId, availability.Detail);
        }

        return await ExecuteControlAsync(awaitable: _lifecycleService.ResumeAsync(new CustomLoopResumeRequest(input.RunId, input.ExpectedLifecycleVersion, input.OperationId, _actor), cancellationToken));
    }

    /// <summary>
    /// Disposes workspace execution ownership and the in-process availability gate.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async ValueTask DisposeAsync()
    {
        await _executionGate.DisposeAsync();
        _executionAvailabilityGate.Dispose();
    }

    private async Task<CustomExecutionAvailability> EnsureCustomExecutionAvailableAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _customExecutionAvailable))
        {
            return _availableCustomExecution;
        }

        // Serialize host reacquisition and recovery. Only one caller may decide whether retained
        // execution is safe to expose again to every later invocation/control caller.
        await _executionAvailabilityGate.WaitAsync(cancellationToken);
        try
        {
            if (_customExecutionAvailable)
            {
                return _availableCustomExecution;
            }

            if (!_customExecutionReacquisitionAllowed)
            {
                return new CustomExecutionAvailability(false, "Failed", "custom_loop_recovery_failed: startup or retained-runtime recovery left unresolved integrity failure, so this runtime cannot automatically reacquire custom-loop execution.");
            }

            var recoveryOperationId = $"runtime-recovery-{Guid.NewGuid():N}";
            var ownership = _executionGate.TryAcquire(recoveryOperationId, new string('0', CustomLoopLimits.Sha256HexCharacters));
            if (ownership.Status == CustomLoopExecutionLeaseStatus.WorkspaceHostUnavailable)
            {
                return new CustomExecutionAvailability(false, "WorkspaceHostUnavailable", ownership.Detail);
            }

            if (ownership.Status == CustomLoopExecutionLeaseStatus.WorkspaceBusy)
            {
                return new CustomExecutionAvailability(false, "WorkspaceHostUnavailable", "workspace_host_unavailable: this retained runtime cannot finish host recovery until the active custom-loop operation reaches a safe boundary; no durable busy outcome was recorded and the request may be retried.");
            }

            if (ownership.Status != CustomLoopExecutionLeaseStatus.Acquired || ownership.Lease is null)
            {
                return new CustomExecutionAvailability(false, "Failed", $"custom_loop_recovery_unavailable: runtime host reacquisition returned {ownership.Status}.");
            }

            using (ownership.Lease)
            {
                IReadOnlyList<CustomLoopRecoveryResult> recovery;
                try
                {
                    recovery = await _recoveryService.RecoverAsync(_actor, cancellationToken);
                }
                catch (UnsupportedCustomLoopRunDiscoveryIndexSchemaException exception)
                {
                    MarkCustomRecoveryRequired();
                    throw new LoopRunEvidenceUnsupportedSchemaException(exception);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Integrity-ambiguous recovery permanently disables automatic reacquisition for
                    // this facade instance; repeated callers must not keep probing unsafe evidence.
                    _customExecutionReacquisitionAllowed = false;
                    _executionGate.RelinquishWorkspaceHost();
                    return new CustomExecutionAvailability(false, "Failed", $"custom_loop_recovery_failed: runtime host reacquisition could not recover interrupted runs safely: {exception.GetType().Name}.");
                }

                if (recovery.Any(result => result.Status is CustomLoopRecoveryStatus.Conflict or CustomLoopRecoveryStatus.Failed))
                {
                    // A failed parking decision is not retry-safe. Relinquish workspace ownership and
                    // require operator intervention instead of reopening dispatch.
                    _customExecutionReacquisitionAllowed = false;
                    _executionGate.RelinquishWorkspaceHost();
                    return new CustomExecutionAvailability(false, "Failed", "custom_loop_recovery_failed: runtime host reacquisition found interrupted work that could not be parked safely.");
                }

                try
                {
                    // Hosting becomes available only after durable transcript state agrees with the
                    // retained in-process conversation; otherwise execution could publish to the wrong history.
                    if (!await _runtimeContext.TryReconcileConversationAsync(cancellationToken))
                    {
                        _executionGate.RelinquishWorkspaceHost();
                        return new CustomExecutionAvailability(false, "Failed", "custom_loop_recovery_failed: durable conversation history diverged from this runtime's active transcript, so local state was preserved, hosting was released, and the request may be retried after the conversation is reconciled.");
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _executionGate.RelinquishWorkspaceHost();
                    return new CustomExecutionAvailability(false, "Failed", $"custom_loop_recovery_failed: durable conversation reconciliation could not be read safely ({exception.GetType().Name}); hosting was released and the request may be retried.");
                }
            }

            Volatile.Write(ref _customExecutionAvailable, true);
            Volatile.Write(ref _customRecoveryRequired, false);
            return _availableCustomExecution;
        }
        finally
        {
            _executionAvailabilityGate.Release();
        }
    }

    private async Task<LoopRunControlResponse> ExecuteControlAsync(Task<CustomLoopControlResult> awaitable)
    {
        return MapControl(await ExecuteControlResultAsync(awaitable));
    }

    private async Task<CustomLoopControlResult> ExecuteControlResultAsync(Task<CustomLoopControlResult> awaitable)
    {
        try
        {
            return await awaitable;
        }
        catch (UnsupportedCustomLoopRunDiscoveryIndexSchemaException exception)
        {
            MarkCustomRecoveryRequired();
            throw new LoopRunEvidenceUnsupportedSchemaException(exception);
        }
    }

    private async Task<CustomLoopOrderedRunResult> ExecuteOrderedRunAsync(Task<CustomLoopOrderedRunResult> awaitable)
    {
        try
        {
            return await awaitable;
        }
        catch (UnsupportedCustomLoopRunDiscoveryIndexSchemaException exception)
        {
            MarkCustomRecoveryRequired();
            throw new LoopRunEvidenceUnsupportedSchemaException(exception);
        }
    }

    private void MarkCustomRecoveryRequired()
    {
        Volatile.Write(ref _customExecutionAvailable, false);
        _customExecutionReacquisitionAllowed = true;
        Volatile.Write(ref _customRecoveryRequired, true);
    }

    private static async Task<T> ExecuteRunStoreReadAsync<T>(Func<Task<T>> read)
    {
        try
        {
            return await read();
        }
        catch (UnsupportedCustomLoopRunDiscoveryIndexSchemaException exception)
        {
            throw new LoopRunEvidenceUnsupportedSchemaException(exception);
        }
    }

    private static LoopRunControlResponse MapControl(CustomLoopControlResult result)
    {
        return new LoopRunControlResponse(result.Status.ToString(), result.Run is null ? null : Map(result.Run), result.OperationId, result.Detail);
    }

    private static bool RequiresCancellationOwnerRecovery(CustomLoopControlResult result)
    {
        return result.Status == CustomLoopControlStatus.Failed
            && result.Run?.Status == CustomLoopRunStatus.CancelRequested
            && result.Detail.Contains("control receipt remains pending", StringComparison.OrdinalIgnoreCase);
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
            CustomLoopInvocationBindingState.Unbound,
            null,
            null,
            now,
            now,
            CustomLoopInvocationOperationState.Pending,
            CustomLoopInvocationOutcome.Unknown,
            string.Empty,
            null,
            [],
            "The canonical custom-loop invocation is durably pending before conversation and context binding.");
    }

    private async Task<LoopRunInvocationResponse> RecordWorkspaceBusyAsync(CustomLoopInvocationOperation operation, CancellationToken cancellationToken)
    {
        try
        {
            var durable = operation;
            if (operation.BindingState == CustomLoopInvocationBindingState.Unbound)
            {
                var begun = await WriteReceiptWithRetentionAsync(token => _invocationOperationStore.BeginAsync(operation, token), cancellationToken);
                if (begun.Status == CustomLoopInvocationOperationStoreStatus.Conflict)
                {
                    return Conflict("The invocation operation id is already bound to different canonical authorized request content.");
                }

                if (IsReceiptWriteFailureStatus(begun.Status))
                {
                    return ReceiptWriteFailure(begun.Status);
                }

                durable = begun.Operation ?? operation;
            }

            var conversationValidation = await ValidateInvocationConversationAsync(durable, cancellationToken);
            if (conversationValidation is not null)
            {
                return conversationValidation;
            }

            if (durable.State == CustomLoopInvocationOperationState.Complete)
            {
                return await ReplayOperationAsync(durable, cancellationToken);
            }

            var detail = durable.BindingState == CustomLoopInvocationBindingState.CapturedContext
                ? "workspace_execution_busy: a captured-context binding from the interrupted invocation was retained, but another custom-loop run is actively executing; no run or provider request was created by this retry."
                : "workspace_execution_busy: another custom-loop run is actively executing; no run, deadline, context snapshot, or provider request was created.";
            if (durable.BindingState == CustomLoopInvocationBindingState.Unbound)
            {
                var binding = await BindOperationAsync(durable, await _runtimeContext.CaptureConversationIdentityAsync(cancellationToken), CustomLoopInvocationBindingState.ConversationWorkspaceExecutionBusy, contextIdentityHash: null, detail, cancellationToken);
                if (binding.Failure is not null)
                {
                    return binding.Failure;
                }

                durable = binding.Operation!;
            }

            var completed = durable with
            {
                UpdatedAtUtc = UtcNow(durable.UpdatedAtUtc),
                State = CustomLoopInvocationOperationState.Complete,
                Outcome = CustomLoopInvocationOutcome.WorkspaceExecutionBusy,
                AdmissionStatus = "WorkspaceExecutionBusy",
                RunId = null,
                Detail = detail
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

    private async Task<InvocationBindingResult> BindOperationAsync(
        CustomLoopInvocationOperation operation,
        string conversationId,
        CustomLoopInvocationBindingState bindingState,
        string? contextIdentityHash,
        string? detail,
        CancellationToken cancellationToken)
    {
        var bound = operation with
        {
            BindingState = bindingState,
            InvokingConversationId = conversationId,
            ContextIdentityHash = contextIdentityHash,
            UpdatedAtUtc = UtcNow(operation.UpdatedAtUtc),
            Detail = detail ?? "The canonical custom-loop invocation is durably bound to its logical conversation and captured-context identity before admission."
        };

        CustomLoopInvocationOperationStoreResult stored;
        try
        {
            stored = await WriteReceiptWithRetentionAsync(token => _invocationOperationStore.BindAsync(bound, token), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new InvocationBindingResult(null, Invalid($"The invocation context binding could not be persisted safely: {exception.GetType().Name}."));
        }

        if (stored.Status == CustomLoopInvocationOperationStoreStatus.Conflict)
        {
            return new InvocationBindingResult(null, Conflict("The invocation operation id is already bound to a different logical conversation or captured-context identity."));
        }

        if (IsReceiptWriteFailureStatus(stored.Status))
        {
            return new InvocationBindingResult(null, ReceiptWriteFailure(stored.Status));
        }

        if (stored.Status == CustomLoopInvocationOperationStoreStatus.NotFound || stored.Operation is null)
        {
            return new InvocationBindingResult(null, Invalid("The pending invocation receipt disappeared before its conversation and context binding could be persisted."));
        }

        if (stored.Operation.State == CustomLoopInvocationOperationState.Complete)
        {
            return new InvocationBindingResult(null, await ReplayOperationAsync(stored.Operation, cancellationToken));
        }

        return new InvocationBindingResult(stored.Operation, null);
    }

    private async Task<LoopRunInvocationResponse?> TryCompletePendingTerminalBindingAsync(CustomLoopInvocationOperation operation, CancellationToken cancellationToken)
    {
        return operation.BindingState switch
        {
            CustomLoopInvocationBindingState.ConversationNotFound => await CompleteRejectedAsync(operation, CustomLoopAdmissionStatus.NotFound.ToString(), null, operation.Detail),
            CustomLoopInvocationBindingState.CapturedContextNotFound => await CompleteRejectedAsync(operation, CustomLoopAdmissionStatus.NotFound.ToString(), null, operation.Detail),
            CustomLoopInvocationBindingState.ConversationWorkspaceExecutionBusy => await RecordWorkspaceBusyAsync(operation, cancellationToken),
            CustomLoopInvocationBindingState.ConversationInvalid => await CompleteRejectedAsync(operation, CustomLoopAdmissionStatus.Invalid.ToString(), null, operation.Detail),
            _ => null
        };
    }

    private async Task<LoopRunInvocationResponse?> ReconcilePendingAdmissionBeforeBusyAsync(CustomLoopInvocationOperation operation, CancellationToken cancellationToken)
    {
        // The admission index is authoritative when the receipt is still pending. Finding a prior run
        // binds and completes that receipt before the newer host's busy outcome is considered.
        CustomLoopRunRecord? run;
        try
        {
            run = await _runStore.GetByAdmissionOperationAsync(operation.OperationId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ReceiptUnavailable($"The pending invocation receipt could not reconcile its prior admission safely: {exception.GetType().Name}; no provider request was dispatched.");
        }

        if (run is null)
        {
            return null;
        }

        var priorRunBinding = await BindPendingOperationToRunAsync(operation, run, cancellationToken);
        if (priorRunBinding.Failure is not null)
        {
            return priorRunBinding.Failure;
        }

        operation = priorRunBinding.Operation!;
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

    private async Task<InvocationBindingResult> BindPendingOperationToRunAsync(CustomLoopInvocationOperation operation, CustomLoopRunRecord run, CancellationToken cancellationToken)
    {
        if (operation.BindingState != CustomLoopInvocationBindingState.Unbound)
        {
            return new InvocationBindingResult(operation, null);
        }

        var identity = await CaptureConversationIdentityAsync(cancellationToken);
        if (identity.Failure is not null)
        {
            return new InvocationBindingResult(null, identity.Failure);
        }

        if (run.InvokingConversation is not null
            && !string.Equals(run.InvokingConversation.ConversationId, identity.ConversationId, StringComparison.Ordinal))
        {
            return new InvocationBindingResult(null, Conflict("The prior admitted run belongs to a different logical conversation."));
        }

        return await BindOperationAsync(operation, identity.ConversationId!, CustomLoopInvocationBindingState.CapturedContext, CustomLoopContextSnapshotHash.ComputeIdentity(run.ContextSnapshot), "The pending invocation receipt was durably bound to its prior admitted run before replay.", cancellationToken);
    }

    private async Task<UndispatchedParkingResult> ParkUndispatchedAdmissionAsync(CustomLoopRunRecord run)
    {
        using var integrity = new CancellationTokenSource(_integrityWriteTimeout);
        try
        {
            var recovery = await _recoveryService.RecoverAsync(_actor, integrity.Token);
            var result = recovery.SingleOrDefault(item => string.Equals(item.Run.Id, run.Id, StringComparison.Ordinal));
            if (result is not null)
            {
                return new UndispatchedParkingResult(result.Run, IsParked(result.Run), result.Detail);
            }

            var reloaded = await _runStore.GetAsync(run.Id, integrity.Token) ?? run;
            return new UndispatchedParkingResult(reloaded, IsParked(reloaded), "Recovery returned no result for the admitted run.");
        }
        catch (Exception exception)
        {
            var reloaded = await TryReloadRunAsync(run.Id) ?? run;
            return new UndispatchedParkingResult(reloaded, IsParked(reloaded), $"Recovery failed with {exception.GetType().Name}.");
        }
    }

    private async Task<LoopRunControlResponse?> TryReplayControlAsync(CustomLoopControlKind kind, LoopRunControlInput input, CancellationToken cancellationToken)
    {
        CustomLoopControlOperation? operation;
        try
        {
            operation = await _controlOperationStore.GetAsync(input.OperationId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new LoopRunControlResponse(CustomLoopControlStatus.Failed.ToString(), null, input.OperationId, $"The control-operation receipt could not be read safely: {exception.GetType().Name}.");
        }

        if (operation is null || operation.State != CustomLoopControlOperationState.Complete)
        {
            return null;
        }

        var requestHash = CustomLoopControlRequestHash.Compute(kind, input.RunId, input.ExpectedLifecycleVersion, input.OperationId, _actor);
        if (!string.Equals(operation.RequestHash, requestHash, StringComparison.Ordinal))
        {
            return new LoopRunControlResponse(CustomLoopControlStatus.Conflict.ToString(), null, input.OperationId, "The operation id is already bound to different control-request content.");
        }

        var run = await TryReloadRunAsync(operation.RunId);
        var replayStatus = ResolveControlReplayStatus(operation, run);
        var detail = replayStatus == operation.Outcome
            ? operation.Detail
            : $"{operation.Detail} The completed Resume operation was replayed from durable run status {run!.Status}; no provider request was dispatched.";
        return new LoopRunControlResponse(replayStatus.ToString(), run is null ? null : Map(run), input.OperationId, detail);
    }

    private static CustomLoopControlStatus ResolveControlReplayStatus(CustomLoopControlOperation operation, CustomLoopRunRecord? run)
    {
        if (operation.Kind != CustomLoopControlKind.Resume || operation.Outcome != CustomLoopControlStatus.Resumed || run is null)
        {
            return operation.Outcome;
        }

        return run.Status switch
        {
            CustomLoopRunStatus.Paused => CustomLoopControlStatus.Paused,
            CustomLoopRunStatus.Cancelled => CustomLoopControlStatus.Cancelled,
            CustomLoopRunStatus.Completed => CustomLoopControlStatus.Completed,
            CustomLoopRunStatus.Failed => CustomLoopControlStatus.Failed,
            CustomLoopRunStatus.NeedsReview => CustomLoopControlStatus.NeedsReview,
            _ => operation.Outcome
        };
    }

    private static bool IsParked(CustomLoopRunRecord run)
    {
        return run.Status == CustomLoopRunStatus.Paused && run.ExecutionClock.ActiveSinceUtc is null;
    }

    private async Task<CustomLoopRunRecord?> TryReloadRunAsync(string runId)
    {
        try
        {
            using var integrity = new CancellationTokenSource(_integrityWriteTimeout);
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
        var bindingMatches = operation.BindingState == CustomLoopInvocationBindingState.CapturedContext
            && (run.InvokingConversation is null || string.Equals(operation.InvokingConversationId, run.InvokingConversation.ConversationId, StringComparison.Ordinal))
            && string.Equals(operation.ContextIdentityHash, CustomLoopContextSnapshotHash.ComputeIdentity(run.ContextSnapshot), StringComparison.Ordinal);
        return promptMatches
            && bindingMatches
            && string.Equals(operation.OperationId, run.AdmissionOperationId, StringComparison.Ordinal)
            && string.Equals(operation.LoopId, run.LoopId, StringComparison.Ordinal)
            && operation.ExpectedDefinitionVersion == run.AdmittedDefinition.DefinitionVersion
            && string.Equals(operation.ExpectedDefinitionHash, run.AdmittedDefinition.ContentHash, StringComparison.Ordinal)
            && string.Equals(operation.Surface, run.Surface, StringComparison.Ordinal)
            && string.Equals(operation.CurrentRoleId, run.AdmittedDefinition.RoleId, StringComparison.Ordinal)
            && string.Equals(operation.Provider, run.ModelSnapshot.Provider, StringComparison.Ordinal)
            && string.Equals(operation.Model, run.ModelSnapshot.Model, StringComparison.Ordinal);
    }

    private async Task<LoopRunInvocationResponse?> ValidateInvocationConversationAsync(CustomLoopInvocationOperation operation, CancellationToken cancellationToken)
    {
        if (operation.BindingState == CustomLoopInvocationBindingState.Unbound)
        {
            return null;
        }

        string conversationId;
        try
        {
            conversationId = await _runtimeContext.CaptureConversationIdentityAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Invalid($"The invocation receipt is bound, but the current logical conversation identity could not be read safely: {exception.GetType().Name}.");
        }

        return string.Equals(operation.InvokingConversationId, conversationId, StringComparison.Ordinal)
            ? null
            : Conflict("The invocation operation id is already bound to a different logical conversation.");
    }

    private async Task<ConversationIdentityResult> CaptureConversationIdentityAsync(CancellationToken cancellationToken)
    {
        try
        {
            return new ConversationIdentityResult(await _runtimeContext.CaptureConversationIdentityAsync(cancellationToken), null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ConversationIdentityResult(null, Invalid($"The current logical conversation identity could not be read safely: {exception.GetType().Name}."));
        }
    }

    private async Task<LoopRunInvocationResponse> CompleteRejectedAsync(
        CustomLoopInvocationOperation operation,
        string admissionStatus,
        CustomLoopRunRecord? run,
        string detail,
        IReadOnlyList<CustomLoopValidationError>? validationErrors = null)
    {
        var completed = await CompleteOperationAsync(operation, CustomLoopInvocationOutcome.Rejected, admissionStatus, run, detail, validationErrors);
        return completed
            ? new LoopRunInvocationResponse(admissionStatus, null, false, run is null ? null : Map(run), (validationErrors ?? []).Select(Map).ToArray(), detail)
            : new LoopRunInvocationResponse(CustomLoopAdmissionStatus.AuditUnavailable.ToString(), null, false, run is null ? null : Map(run), (validationErrors ?? []).Select(Map).ToArray(), "The invocation was rejected, but its strict operation receipt could not be completed safely; no provider request was dispatched.");
    }

    private async Task<bool> CompleteOperationAsync(CustomLoopInvocationOperation operation, CustomLoopInvocationOutcome outcome, string admissionStatus, CustomLoopRunRecord? run, string detail, IReadOnlyList<CustomLoopValidationError>? validationErrors = null)
    {
        var completed = operation with
        {
            UpdatedAtUtc = UtcNow(operation.UpdatedAtUtc),
            State = CustomLoopInvocationOperationState.Complete,
            Outcome = outcome,
            AdmissionStatus = admissionStatus,
            RunId = run?.Id,
            ValidationErrors = (validationErrors ?? []).ToArray(),
            Detail = detail
        };
        return await CompleteReceiptAsync(completed);
    }

    private async Task<bool> CompleteReceiptAsync(CustomLoopInvocationOperation completed)
    {
        using var integrity = new CancellationTokenSource(_integrityWriteTimeout);
        try
        {
            var result = await WriteReceiptWithRetentionAsync(token => _invocationOperationStore.CompleteAsync(completed, token), integrity.Token);
            return result.Status is CustomLoopInvocationOperationStoreStatus.Completed or CustomLoopInvocationOperationStoreStatus.Replayed;
        }
        catch
        {
            return false;
        }
    }

    private async Task<CustomLoopInvocationOperationStoreResult> WriteReceiptWithRetentionAsync(
        Func<CancellationToken, Task<CustomLoopInvocationOperationStoreResult>> write,
        CancellationToken cancellationToken)
    {
        var result = await write(cancellationToken);
        if (result.Status is not (CustomLoopInvocationOperationStoreStatus.LimitExceeded or CustomLoopInvocationOperationStoreStatus.RetentionRequired))
        {
            return result;
        }

        // Capacity is reclaimed only through governed retention. The original write is retried once
        // after retention succeeds so no caller can bypass replay and audit guarantees.
        var retention = await _invocationReceiptRetention.PruneForCapacityAsync(_actor, _surface, cancellationToken);
        if (!retention.AllowsReceiptWrite)
        {
            var status = retention.Status switch
            {
                CustomLoopInvocationReceiptRetentionStatus.OperationInProgress => CustomLoopInvocationOperationStoreStatus.RetentionRequired,
                CustomLoopInvocationReceiptRetentionStatus.AuditUnavailable => CustomLoopInvocationOperationStoreStatus.RetentionAuditUnavailable,
                CustomLoopInvocationReceiptRetentionStatus.Invalid => CustomLoopInvocationOperationStoreStatus.RetentionInvalid,
                _ => CustomLoopInvocationOperationStoreStatus.LimitExceeded
            };
            return new CustomLoopInvocationOperationStoreResult(status, result.Operation);
        }

        return await write(cancellationToken);
    }

    internal static LoopRunInvocationResponse ReceiptWriteFailure(CustomLoopInvocationOperationStoreStatus status)
    {
        return status switch
        {
            CustomLoopInvocationOperationStoreStatus.RetentionRequired => new LoopRunInvocationResponse("OperationInProgress", null, false, null, [], "A governed completed-receipt retention operation is still inside its bounded ownership window; retry later."),
            CustomLoopInvocationOperationStoreStatus.RetentionAuditUnavailable => new LoopRunInvocationResponse("AuditUnavailable", null, false, null, [], "Expired completed invocation receipts were eligible, but governed retention could not establish durable audit integrity; no run or provider request was created."),
            CustomLoopInvocationOperationStoreStatus.RetentionInvalid => new LoopRunInvocationResponse("Invalid", null, false, null, [], "Invocation receipt retention state is invalid or corrupt and requires operator review; no run or provider request was created."),
            _ => new LoopRunInvocationResponse("LimitExceeded", null, false, null, [], $"Invocation receipts reached their governed workspace quota and no completed receipt was eligible beyond the {CustomLoopInvocationReceiptRetentionPolicy.MinimumReplayDuration.TotalDays:0}-day replay boundary; no run or provider request was created.")
        };
    }

    private static bool IsReceiptWriteFailureStatus(CustomLoopInvocationOperationStoreStatus status)
    {
        return status is CustomLoopInvocationOperationStoreStatus.LimitExceeded
            or CustomLoopInvocationOperationStoreStatus.RetentionRequired
            or CustomLoopInvocationOperationStoreStatus.RetentionAuditUnavailable
            or CustomLoopInvocationOperationStoreStatus.RetentionInvalid;
    }

    private async Task<LoopRunInvocationResponse> ReplayOperationAsync(CustomLoopInvocationOperation operation, CancellationToken cancellationToken)
    {
        // A receipt is not sufficient by itself: its run or deletion tombstone must still bind to the
        // same conversation, context, and status-specific outcome before replay returns success.
        LoopRunSnapshot? run = null;
        CustomLoopRunRecord? durableRun = null;
        CustomLoopTraceInspection? deletedTrace = null;
        if (operation.RunId is not null)
        {
            try
            {
                durableRun = await _runStore.GetAsync(operation.RunId, cancellationToken);
                run = durableRun is null ? null : Map(durableRun);
                if (durableRun is null && operation.Outcome == CustomLoopInvocationOutcome.Rejected)
                {
                    var inspected = await _runStore.InspectTraceAsync(operation.RunId, cancellationToken);
                    deletedTrace = inspected is { IsDeleted: true, Tombstone: not null } ? inspected : null;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return ReceiptUnavailable($"The invocation receipt was found, but its run could not be read safely: {exception.GetType().Name}.");
            }
        }

        if (operation.Outcome == CustomLoopInvocationOutcome.WorkspaceExecutionBusy)
        {
            var detail = operation.BindingState == CustomLoopInvocationBindingState.CapturedContext
                ? "The durable workspace_execution_busy outcome was replayed with its prior captured-context binding; no run or provider dispatch was attempted by the retry."
                : "The durable workspace_execution_busy outcome was replayed; no run, context capture, or provider dispatch was attempted.";
            return Busy(detail);
        }

        if (operation.RunId is not null && durableRun is null && deletedTrace is null)
        {
            if (operation.Outcome == CustomLoopInvocationOutcome.Admitted)
            {
                return ReceiptUnavailable("The durable admitted invocation receipt refers to a missing run; no provider request was dispatched.");
            }

            return Invalid($"The durable {operation.AdmissionStatus} invocation receipt refers to a missing run; no provider request was dispatched.");
        }

        if (operation.Outcome == CustomLoopInvocationOutcome.Admitted && durableRun is not null && !InvocationMatchesRun(operation, durableRun))
        {
            return Conflict("The durable invocation receipt does not match its run's bound conversation and captured-context identity; no provider request was dispatched.");
        }

        if (operation.Outcome == CustomLoopInvocationOutcome.Rejected && durableRun is not null && !RejectedInvocationReferencesRun(operation, durableRun))
        {
            return Conflict("The durable rejected invocation receipt does not match its status-specific run reference; no provider request was dispatched.");
        }

        if (operation.Outcome == CustomLoopInvocationOutcome.Rejected && deletedTrace is not null && !RejectedInvocationReferencesTrace(operation, deletedTrace))
        {
            return Conflict("The durable rejected invocation receipt does not match its status-specific deleted-run reference; no provider request was dispatched.");
        }

        return new LoopRunInvocationResponse(
            operation.AdmissionStatus,
            durableRun?.Status.ToString() ?? deletedTrace?.TerminalStatus.ToString(),
            false,
            run,
            operation.ValidationErrors.Select(Map).ToArray(),
            operation.Outcome == CustomLoopInvocationOutcome.Admitted
                ? "The durable admitted invocation outcome was replayed without another provider dispatch."
                : $"{operation.Detail} The durable {operation.AdmissionStatus} invocation outcome was replayed without context capture or provider dispatch.");
    }

    private DateTimeOffset UtcNow(DateTimeOffset minimum)
    {
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        return now < minimum ? minimum : now;
    }

    private static bool RejectedInvocationReferencesRun(CustomLoopInvocationOperation operation, CustomLoopRunRecord run)
    {
        return operation.AdmissionStatus switch
        {
            nameof(CustomLoopAdmissionStatus.NonterminalRunExists) => string.Equals(operation.LoopId, run.LoopId, StringComparison.Ordinal),
            nameof(CustomLoopAdmissionStatus.Conflict) => string.Equals(operation.OperationId, run.AdmissionOperationId, StringComparison.Ordinal),
            nameof(CustomLoopAdmissionStatus.AuditUnavailable) => InvocationMatchesRun(operation, run)
                || string.Equals(operation.LoopId, run.LoopId, StringComparison.Ordinal)
                || string.Equals(operation.OperationId, run.AdmissionOperationId, StringComparison.Ordinal),
            nameof(CustomLoopAdmissionStatus.Invalid) => string.Equals(operation.LoopId, run.LoopId, StringComparison.Ordinal),
            _ => false
        };
    }

    private static bool RejectedInvocationReferencesTrace(CustomLoopInvocationOperation operation, CustomLoopTraceInspection trace)
    {
        var tombstone = trace.Tombstone!;
        return operation.AdmissionStatus switch
        {
            nameof(CustomLoopAdmissionStatus.NonterminalRunExists) => string.Equals(operation.LoopId, trace.LoopId, StringComparison.Ordinal),
            nameof(CustomLoopAdmissionStatus.Conflict) => string.Equals(operation.OperationId, tombstone.AdmissionOperationId, StringComparison.Ordinal),
            nameof(CustomLoopAdmissionStatus.AuditUnavailable) => string.Equals(operation.LoopId, trace.LoopId, StringComparison.Ordinal)
                || string.Equals(operation.OperationId, tombstone.AdmissionOperationId, StringComparison.Ordinal),
            nameof(CustomLoopAdmissionStatus.Invalid) => string.Equals(operation.LoopId, trace.LoopId, StringComparison.Ordinal),
            _ => false
        };
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

    private sealed record UndispatchedParkingResult(CustomLoopRunRecord Run, bool IsParked, string Detail);

    private sealed record InvocationBindingResult(CustomLoopInvocationOperation? Operation, LoopRunInvocationResponse? Failure);

    private sealed record ConversationIdentityResult(string? ConversationId, LoopRunInvocationResponse? Failure);

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

    private static LoopRunInvocationResponse ReceiptUnavailable(string detail)
    {
        return new LoopRunInvocationResponse(CustomLoopAdmissionStatus.ReceiptUnavailable.ToString(), null, false, null, [], detail);
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
            summary.LifecycleVersion,
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
                run.ContextSnapshot.WorkspaceContextMessages.Select(Map).ToArray(),
                run.ContextSnapshot.InvokingConversationMessages.Select(Map).ToArray()),
            new LoopRunExecutionClockSnapshot(run.ExecutionClock.AccumulatedRunningMilliseconds, run.ExecutionClock.ActiveSinceUtc),
            Map(run.Checkpoint),
            run.Events.Select(Map).ToArray(),
            run.FinalOutput,
            run.FailureCode,
            run.FailureDetail)
        {
            ConversationPublicationDispositions = LoopRunConversationPublicationDispositionProjector.Project(run)
        };
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
