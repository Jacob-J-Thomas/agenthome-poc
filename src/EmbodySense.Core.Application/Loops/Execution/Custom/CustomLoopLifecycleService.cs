using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Governance.Audit.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using System.Text;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

public sealed class CustomLoopLifecycleService
{
    private static readonly TimeSpan IntegrityWriteTimeout = TimeSpan.FromSeconds(30);

    private readonly ICustomLoopRunStore _runStore;
    private readonly ICustomLoopControlOperationStore _operationStore;
    private readonly ICustomLoopResumeExecutor _resumeExecutor;
    private readonly ICustomLoopModelAvailability _modelAvailability;
    private readonly ICustomLoopExecutionCancellationSignal _cancellationSignal;
    private readonly IAuditLog _auditLog;
    private readonly ICustomLoopWorkspaceExecutionGate _executionGate;
    private readonly TimeProvider _timeProvider;

    public CustomLoopLifecycleService(
        ICustomLoopRunStore runStore,
        ICustomLoopControlOperationStore operationStore,
        ICustomLoopResumeExecutor resumeExecutor,
        ICustomLoopModelAvailability modelAvailability,
        ICustomLoopExecutionCancellationSignal cancellationSignal,
        IAuditLog auditLog,
        ICustomLoopWorkspaceExecutionGate executionGate,
        TimeProvider? timeProvider = null)
    {
        _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
        _operationStore = operationStore ?? throw new ArgumentNullException(nameof(operationStore));
        _resumeExecutor = resumeExecutor ?? throw new ArgumentNullException(nameof(resumeExecutor));
        _modelAvailability = modelAvailability ?? throw new ArgumentNullException(nameof(modelAvailability));
        _cancellationSignal = cancellationSignal ?? throw new ArgumentNullException(nameof(cancellationSignal));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _executionGate = executionGate ?? throw new ArgumentNullException(nameof(executionGate));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<CustomLoopControlResult> PauseAsync(CustomLoopPauseRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(CustomLoopControlKind.Pause, request.RunId, request.ExpectedLifecycleVersion, request.OperationId, request.Actor, cancellationToken);
    }

    public Task<CustomLoopControlResult> CancelAsync(CustomLoopCancelRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(CustomLoopControlKind.Cancel, request.RunId, request.ExpectedLifecycleVersion, request.OperationId, request.Actor, cancellationToken);
    }

    public Task<CustomLoopControlResult> ResumeAsync(CustomLoopResumeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(CustomLoopControlKind.Resume, request.RunId, request.ExpectedLifecycleVersion, request.OperationId, request.Actor, cancellationToken);
    }

    private async Task<CustomLoopControlResult> ExecuteAsync(CustomLoopControlKind kind, string runId, int expectedLifecycleVersion, string operationId, string actor, CancellationToken cancellationToken)
    {
        ValidateRequest(runId, expectedLifecycleVersion, operationId, actor);
        var now = UtcNow();
        var pending = new CustomLoopControlOperation(
            CustomLoopControlOperation.CurrentSchemaVersion,
            operationId,
            CustomLoopControlRequestHash.Compute(kind, runId, expectedLifecycleVersion, operationId, actor),
            kind,
            runId,
            expectedLifecycleVersion,
            actor,
            now,
            now,
            CustomLoopControlOperationState.Pending,
            CustomLoopControlStatus.Unknown,
            null,
            null,
            false,
            "The custom-loop control operation is durably pending.");

        CustomLoopControlOperationStoreResult begun;
        try
        {
            begun = await _operationStore.BeginAsync(pending, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Result(CustomLoopControlStatus.Failed, null, operationId, $"The control-operation receipt could not be started safely: {SafeExceptionClass(exception)}.");
        }

        if (begun.Status == CustomLoopControlOperationStoreStatus.Conflict)
        {
            return Result(CustomLoopControlStatus.Conflict, null, operationId, "The operation id is already bound to different control-request content.");
        }

        var operation = begun.Operation ?? pending;
        if (begun.Status == CustomLoopControlOperationStoreStatus.Replayed && operation.State == CustomLoopControlOperationState.Complete)
        {
            var replayRun = await TryLoadAsync(runId, cancellationToken);
            if (operation.Outcome == CustomLoopControlStatus.WorkspaceExecutionBusy)
            {
                return Result(CustomLoopControlStatus.WorkspaceExecutionBusy, replayRun, operationId, "The durable workspace_execution_busy outcome was replayed without lifecycle mutation or provider dispatch.");
            }

            return Result(CustomLoopControlStatus.Replayed, replayRun, operationId, $"The durable {operation.Outcome} control outcome was replayed without another mutation or dispatch.");
        }

        CustomLoopRunRecord? run;
        try
        {
            run = await _runStore.GetAsync(runId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return await CompleteAsync(operation, CustomLoopControlStatus.Failed, null, false, $"The run could not be loaded safely: {SafeExceptionClass(exception)}.");
        }

        if (run is null)
        {
            return await CompleteAsync(operation, CustomLoopControlStatus.NotFound, null, true, "The custom-loop run does not exist.");
        }

        var validation = CustomLoopRunValidator.Validate(run);
        if (!validation.IsValid)
        {
            return await CompleteAsync(operation, CustomLoopControlStatus.InvalidState, run, true, "The persisted custom-loop run is invalid; no lifecycle mutation was attempted.");
        }

        if (run.Events.Any(item => string.Equals(item.EventId, operationId, StringComparison.Ordinal)))
        {
            return await RecoverPendingReceiptAsync(operation, run);
        }

        if (run.LifecycleVersion != expectedLifecycleVersion)
        {
            return await CompleteAsync(operation, CustomLoopControlStatus.Conflict, run, true, $"Expected lifecycle version {expectedLifecycleVersion}, but the durable run is version {run.LifecycleVersion}.");
        }

        return kind switch
        {
            CustomLoopControlKind.Pause => await PauseCoreAsync(operation, run),
            CustomLoopControlKind.Cancel => await CancelCoreAsync(operation, run),
            CustomLoopControlKind.Resume => await ResumeCoreAsync(operation, run, cancellationToken),
            _ => await CompleteAsync(operation, CustomLoopControlStatus.InvalidState, run, true, "The control kind is unsupported.")
        };
    }

    private async Task<CustomLoopControlResult> PauseCoreAsync(CustomLoopControlOperation operation, CustomLoopRunRecord run)
    {
        if (run.Status == CustomLoopRunStatus.Paused)
        {
            return await CompleteAsync(operation, CustomLoopControlStatus.Paused, run, true, "The run is already safely Paused at a committed boundary; no mutation was repeated.");
        }

        if (run.Status == CustomLoopRunStatus.PauseRequested)
        {
            return await CompleteAsync(operation, CustomLoopControlStatus.PauseRequested, run, true, "A pause is already requested; the runner will stop only after a proved checkpoint boundary.");
        }

        if (run.Status != CustomLoopRunStatus.Running)
        {
            return await CompleteAsync(operation, CustomLoopControlStatus.InvalidState, run, true, $"Pause is allowed only from Running or as an idempotent request against PauseRequested or Paused, not {run.Status}.");
        }

        var transition = await PersistTransitionAsync(run, CustomLoopRunStatus.PauseRequested, operation.Actor, operation.OperationId, "Pause was requested; an open attempt may finish, but no later attempt may start.");
        if (transition.Run is null)
        {
            return await CompleteAsync(operation, transition.Status, transition.CurrentRun, true, transition.Detail);
        }

        var outcome = transition.AuditRecorded ? CustomLoopControlStatus.PauseRequested : CustomLoopControlStatus.AuditWarning;
        return await CompleteAsync(operation, outcome, transition.Run, transition.AuditRecorded, transition.Detail);
    }

    private async Task<CustomLoopControlResult> CancelCoreAsync(CustomLoopControlOperation operation, CustomLoopRunRecord run)
    {
        if (run.Status == CustomLoopRunStatus.CancelRequested)
        {
            if (run.ExecutionClock.ActiveSinceUtc is null)
            {
                var cancelled = await PersistTransitionAsync(run, CustomLoopRunStatus.Cancelled, operation.Actor, operation.OperationId, "The safely stopped cancellation request was completed without provider dispatch.");
                var cancelledOutcome = cancelled.Run is null ? cancelled.Status : cancelled.AuditRecorded ? CustomLoopControlStatus.Cancelled : CustomLoopControlStatus.AuditWarning;
                return await CompleteAsync(operation, cancelledOutcome, cancelled.Run ?? cancelled.CurrentRun, cancelled.AuditRecorded, cancelled.Detail);
            }

            if (!TryCancelActiveAttempt(run.Id, out var signalFailure))
            {
                return Result(CustomLoopControlStatus.Failed, run, operation.OperationId, signalFailure);
            }

            return await CompleteAsync(operation, CustomLoopControlStatus.CancelRequested, run, true, "Cancellation is already requested; no new provider dispatch is permitted.");
        }

        if (run.Status is CustomLoopRunStatus.Running or CustomLoopRunStatus.PauseRequested)
        {
            var requested = await PersistTransitionAsync(run, CustomLoopRunStatus.CancelRequested, operation.Actor, operation.OperationId, "Cancellation was requested; any open provider attempt is being cancelled and no later attempt may start.");
            if (requested.Run is null)
            {
                return await CompleteAsync(operation, requested.Status, requested.CurrentRun, true, requested.Detail);
            }

            if (!TryCancelActiveAttempt(run.Id, out var signalFailure))
            {
                return Result(CustomLoopControlStatus.Failed, requested.Run, operation.OperationId, signalFailure);
            }

            var outcome = requested.AuditRecorded ? CustomLoopControlStatus.CancelRequested : CustomLoopControlStatus.AuditWarning;
            return await CompleteAsync(operation, outcome, requested.Run, requested.AuditRecorded, requested.Detail);
        }

        if (run.Status == CustomLoopRunStatus.Admitted)
        {
            var cancelled = await PersistTransitionAsync(run, CustomLoopRunStatus.Cancelled, operation.Actor, operation.OperationId, "The admitted run was cancelled before provider dispatch.");
            var outcome = cancelled.Run is null ? cancelled.Status : cancelled.AuditRecorded ? CustomLoopControlStatus.Cancelled : CustomLoopControlStatus.AuditWarning;
            return await CompleteAsync(operation, outcome, cancelled.Run ?? cancelled.CurrentRun, cancelled.AuditRecorded, cancelled.Detail);
        }

        if (run.Status == CustomLoopRunStatus.Paused)
        {
            var cancelled = await PersistTransitionAsync(run, CustomLoopRunStatus.Cancelled, operation.Actor, operation.OperationId, "The safely paused run was cancelled atomically without provider dispatch.");
            var outcome = cancelled.Run is null ? cancelled.Status : cancelled.AuditRecorded ? CustomLoopControlStatus.Cancelled : CustomLoopControlStatus.AuditWarning;
            return await CompleteAsync(operation, outcome, cancelled.Run ?? cancelled.CurrentRun, cancelled.AuditRecorded, cancelled.Detail);
        }

        return await CompleteAsync(operation, CustomLoopControlStatus.InvalidState, run, true, $"Cancel cannot mutate terminal state {run.Status}.");
    }

    private async Task<CustomLoopControlResult> ResumeCoreAsync(CustomLoopControlOperation operation, CustomLoopRunRecord run, CancellationToken cancellationToken)
    {
        if (run.Status != CustomLoopRunStatus.Paused)
        {
            return await CompleteAsync(operation, CustomLoopControlStatus.InvalidState, run, true, $"Explicit Resume is allowed only from Paused, not {run.Status}.");
        }

        bool modelAvailable;
        try
        {
            modelAvailable = await _modelAvailability.IsAvailableAsync(run.ModelSnapshot, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return await CompleteAsync(operation, CustomLoopControlStatus.Failed, run, false, $"Resume could not verify that the admitted provider/model remains available: {SafeExceptionClass(exception)}. The run remains Paused and no provider request was dispatched.");
        }

        if (!modelAvailable)
        {
            return await CompleteAsync(operation, CustomLoopControlStatus.InvalidState, run, true, "Resume rejected because the admitted provider/model is not available in the current runtime configuration. The run remains Paused and no provider request was dispatched; no fallback or model switch was attempted.");
        }

        if (!CustomLoopRunValidator.HasCompleteAdmissionAudit(run))
        {
            const string detail = "Explicit Resume rejected an integrity-incomplete admission; the run requires review and no provider request was dispatched.";
            var quarantined = await PersistTransitionAsync(run, CustomLoopRunStatus.NeedsReview, operation.Actor, operation.OperationId, detail);
            var quarantinedRun = quarantined.Run ?? quarantined.CurrentRun ?? run;
            var status = quarantined.Run is null ? quarantined.Status : CustomLoopControlStatus.NeedsReview;
            return await CompleteAsync(operation, status, quarantinedRun, quarantined.AuditRecorded, quarantined.Detail);
        }

        var gate = _executionGate.TryAcquire(operation.OperationId, operation.RequestHash);
        if (gate.Status == CustomLoopExecutionLeaseStatus.WorkspaceBusy)
        {
            return await CompleteAsync(operation, CustomLoopControlStatus.WorkspaceExecutionBusy, run, false, "workspace_execution_busy: another custom-loop run is actively executing; the Paused run and its deadline, checkpoint, and approval binding were not changed.");
        }

        if (gate.Status == CustomLoopExecutionLeaseStatus.OperationInProgress)
        {
            return Result(CustomLoopControlStatus.OperationInProgress, run, operation.OperationId, "The same Resume operation is already executing; no second lifecycle mutation or provider dispatch was attempted.");
        }

        if (gate.Status == CustomLoopExecutionLeaseStatus.OperationConflict || gate.Lease is null)
        {
            return Result(CustomLoopControlStatus.Conflict, run, operation.OperationId, "The Resume operation could not acquire canonical workspace execution ownership.");
        }

        using (gate.Lease)
        {
            var resumed = await PersistTransitionAsync(run, CustomLoopRunStatus.Running, operation.Actor, operation.OperationId, "Explicit Resume admitted the persisted checkpoint for ordered execution.");
            if (resumed.Run is null)
            {
                return await CompleteAsync(operation, resumed.Status, resumed.CurrentRun, true, resumed.Detail);
            }

            var receiptStatus = resumed.AuditRecorded ? CustomLoopControlStatus.Resumed : CustomLoopControlStatus.AuditWarning;
            var receipt = await CompleteAsync(operation, receiptStatus, resumed.Run, resumed.AuditRecorded, resumed.Detail);
            if (!resumed.AuditRecorded || receipt.Status is CustomLoopControlStatus.NeedsReview or CustomLoopControlStatus.Failed)
            {
                var detail = "Resume stopped before provider dispatch because its transition audit or durable idempotency receipt was incomplete. The run is being parked at a safe checkpoint boundary.";
                var parked = await PersistTransitionAsync(resumed.Run, CustomLoopRunStatus.Paused, operation.Actor, NewEventId("resume-park"), detail);
                var parkedRun = parked.Run ?? parked.CurrentRun ?? resumed.Run;
                return Result(CustomLoopControlStatus.NeedsReview, parkedRun, operation.OperationId, parked.Run is null ? $"{receipt.Detail} The undispatched Running transition could not be parked automatically." : detail);
            }

            CustomLoopOrderedRunResult execution;
            try
            {
                execution = await _resumeExecutor.ResumeAsync(new CustomLoopResumeExecutionRequest(resumed.Run.Id, resumed.Run.LifecycleVersion, operation.OperationId, operation.Actor), cancellationToken);
            }
            catch (Exception exception)
            {
                return await HandleResumeExecutorFailureAsync(operation, resumed.Run, exception);
            }

            return execution.Status switch
            {
                CustomLoopOrderedRunStatus.Completed => Result(CustomLoopControlStatus.Completed, execution.Run, operation.OperationId, execution.Detail),
                CustomLoopOrderedRunStatus.Cancelled => Result(CustomLoopControlStatus.Cancelled, execution.Run, operation.OperationId, execution.Detail),
                CustomLoopOrderedRunStatus.Paused => Result(CustomLoopControlStatus.Paused, execution.Run, operation.OperationId, execution.Detail),
                CustomLoopOrderedRunStatus.NeedsReview => Result(CustomLoopControlStatus.NeedsReview, execution.Run, operation.OperationId, execution.Detail),
                CustomLoopOrderedRunStatus.Conflict => Result(CustomLoopControlStatus.Conflict, execution.Run, operation.OperationId, execution.Detail),
                CustomLoopOrderedRunStatus.NotFound => Result(CustomLoopControlStatus.NotFound, execution.Run, operation.OperationId, execution.Detail),
                CustomLoopOrderedRunStatus.InvalidState => Result(CustomLoopControlStatus.InvalidState, execution.Run, operation.OperationId, execution.Detail),
                _ => Result(CustomLoopControlStatus.Failed, execution.Run, operation.OperationId, execution.Detail)
            };
        }
    }

    private async Task<CustomLoopControlResult> RecoverPendingReceiptAsync(CustomLoopControlOperation operation, CustomLoopRunRecord run)
    {
        if (operation.Kind == CustomLoopControlKind.Cancel && run.Status == CustomLoopRunStatus.CancelRequested)
        {
            if (run.ExecutionClock.ActiveSinceUtc is null)
            {
                var cancelled = await PersistTransitionAsync(run, CustomLoopRunStatus.Cancelled, operation.Actor, NewEventId("cancel-recovery"), "Pending cancellation recovery proved that no provider attempt was active and completed cancellation without dispatch.");
                var cancelledOutcome = cancelled.Run is null ? cancelled.Status : cancelled.AuditRecorded ? CustomLoopControlStatus.Cancelled : CustomLoopControlStatus.AuditWarning;
                return await CompleteAsync(operation, cancelledOutcome, cancelled.Run ?? cancelled.CurrentRun, cancelled.AuditRecorded, cancelled.Detail);
            }

            if (!TryCancelActiveAttempt(run.Id, out var signalFailure))
            {
                return Result(CustomLoopControlStatus.Failed, run, operation.OperationId, signalFailure);
            }
        }

        var status = StatusFor(run.Status, operation.Kind);
        var detail = "A previously committed lifecycle transition was found by operation event id; its pending receipt was recovered without another mutation or dispatch.";
        var auditRecorded = await TryAuditAsync(operation.Actor, run, operation.Kind, run.Status, detail, recoveredReceipt: true);
        var completionStatus = auditRecorded ? status : CustomLoopControlStatus.AuditWarning;
        var completed = await CompleteAsync(operation, completionStatus, run, auditRecorded, detail);
        return completed.Status == completionStatus && auditRecorded ? Result(CustomLoopControlStatus.Replayed, run, operation.OperationId, detail) : completed;
    }

    private async Task<CustomLoopControlResult> HandleResumeExecutorFailureAsync(CustomLoopControlOperation operation, CustomLoopRunRecord resumedRun, Exception exception)
    {
        var detail = $"The ordered resume executor failed after the durable Running transition: {SafeExceptionClass(exception)}. Execution is stopped for review instead of remaining silently Running.";
        var current = await TryLoadAsync(resumedRun.Id, IntegrityToken()) ?? resumedRun;
        if (current.Status == CustomLoopRunStatus.Paused)
        {
            return Result(CustomLoopControlStatus.Paused, current, operation.OperationId, detail);
        }

        if (current.IsTerminal)
        {
            return Result(StatusFor(current.Status, CustomLoopControlKind.Resume), current, operation.OperationId, detail);
        }

        var quarantined = await PersistTransitionAsync(current, CustomLoopRunStatus.NeedsReview, operation.Actor, NewEventId("resume-failure"), detail);
        var quarantinedRun = quarantined.Run ?? quarantined.CurrentRun ?? current;
        return Result(CustomLoopControlStatus.NeedsReview, quarantinedRun, operation.OperationId, quarantined.Run is null ? $"{detail} {quarantined.Detail}" : quarantined.Detail);
    }

    private bool TryCancelActiveAttempt(string runId, out string failureDetail)
    {
        try
        {
            _cancellationSignal.CancelActiveAttempt(runId);
            failureDetail = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            failureDetail = $"The cancellation request is durable, but signalling the active attempt failed: {SafeExceptionClass(exception)}. The control receipt remains pending so the same operation can retry the signal.";
            return false;
        }
    }

    private async Task<TransitionResult> PersistTransitionAsync(CustomLoopRunRecord current, CustomLoopRunStatus status, string actor, string eventId, string detail)
    {
        var now = Now(current);
        var candidate = CreateTransition(current, status, eventId, detail, now);
        CustomLoopRunStoreResult stored;
        try
        {
            stored = await _runStore.UpdateAsync(candidate, current.LifecycleVersion, IntegrityToken());
        }
        catch (Exception exception)
        {
            return new TransitionResult(null, current, CustomLoopControlStatus.Failed, false, $"The lifecycle trace update failed: {SafeExceptionClass(exception)}.");
        }

        if (stored.Status != CustomLoopRunStoreStatus.Updated || stored.Run is null)
        {
            var latest = await TryLoadAsync(current.Id, IntegrityToken());
            var controlStatus = stored.Status is CustomLoopRunStoreStatus.Conflict or CustomLoopRunStoreStatus.TerminalImmutable ? CustomLoopControlStatus.Conflict : stored.Status == CustomLoopRunStoreStatus.NotFound ? CustomLoopControlStatus.NotFound : CustomLoopControlStatus.Failed;
            return new TransitionResult(null, latest ?? current, controlStatus, false, "The lifecycle transition lost compare-and-swap or was rejected; no retry was attempted.");
        }

        var auditRecorded = await TryAuditAsync(actor, stored.Run, KindForTransition(current.Status, status), status, detail, recoveredReceipt: false);
        var persistedDetail = auditRecorded ? detail : $"{detail} The transition is durable, but its matching audit append failed.";
        return new TransitionResult(stored.Run, stored.Run, StatusFor(status, KindForTransition(current.Status, status)), auditRecorded, persistedDetail);
    }

    private async Task<bool> TryAuditAsync(string actor, CustomLoopRunRecord run, CustomLoopControlKind kind, CustomLoopRunStatus status, string detail, bool recoveredReceipt)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["runId"] = run.Id,
            ["loopId"] = run.LoopId,
            ["definitionVersion"] = run.AdmittedDefinition.DefinitionVersion,
            ["definitionHash"] = run.AdmittedDefinition.ContentHash,
            ["controlKind"] = kind.ToString().ToLowerInvariant(),
            ["runStatus"] = status.ToString().ToLowerInvariant(),
            ["lifecycleVersion"] = run.LifecycleVersion,
            ["recoveredReceipt"] = recoveredReceipt
        };
        var outcome = status switch
        {
            CustomLoopRunStatus.PauseRequested or CustomLoopRunStatus.CancelRequested => AuditSchema.Outcomes.Requested,
            CustomLoopRunStatus.NeedsReview => AuditSchema.Outcomes.NeedsReview,
            _ => AuditSchema.Outcomes.Succeeded
        };

        try
        {
            await _auditLog.AppendAsync(AuditEvent.Create(actor, AuditSchema.Actions.LoopRunLifecycle, run.Id, outcome, detail, metadata), IntegrityToken());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<CustomLoopControlResult> CompleteAsync(CustomLoopControlOperation operation, CustomLoopControlStatus status, CustomLoopRunRecord? run, bool auditRecorded, string detail)
    {
        var completed = operation with
        {
            UpdatedAtUtc = UtcNow(operation.UpdatedAtUtc),
            State = CustomLoopControlOperationState.Complete,
            Outcome = status,
            ResultLifecycleVersion = run?.LifecycleVersion,
            ResultRunStatus = run?.Status,
            OutcomeAuditRecorded = auditRecorded,
            Detail = detail
        };

        try
        {
            var stored = await _operationStore.CompleteAsync(completed, IntegrityToken());
            if (stored.Status is CustomLoopControlOperationStoreStatus.Completed or CustomLoopControlOperationStoreStatus.Replayed)
            {
                return Result(status, run, operation.OperationId, detail);
            }

            return Result(CustomLoopControlStatus.NeedsReview, run, operation.OperationId, "The lifecycle outcome may be durable, but its idempotency receipt could not be completed safely.");
        }
        catch (Exception exception)
        {
            return Result(CustomLoopControlStatus.NeedsReview, run, operation.OperationId, $"The lifecycle outcome may be durable, but its idempotency receipt failed: {SafeExceptionClass(exception)}.");
        }
    }

    private static CustomLoopRunRecord CreateTransition(CustomLoopRunRecord run, CustomLoopRunStatus status, string eventId, string detail, DateTimeOffset now)
    {
        var isTerminal = status is CustomLoopRunStatus.Completed or CustomLoopRunStatus.Failed or CustomLoopRunStatus.Cancelled or CustomLoopRunStatus.NeedsReview;
        var clock = status switch
        {
            CustomLoopRunStatus.Running when run.Status == CustomLoopRunStatus.Paused => run.ExecutionClock with { ActiveSinceUtc = now },
            CustomLoopRunStatus.Paused => StopClock(run.ExecutionClock, now),
            _ when isTerminal => StopClock(run.ExecutionClock, now),
            _ => run.ExecutionClock
        };
        var lifecycle = new CustomLoopRunEvent(run.Events.Length + 1, eventId, now, CustomLoopRunEventKind.LifecycleChanged, null, null, null, detail, [], null, null, null, null, null, null, null, null, null, null);
        return run with
        {
            LifecycleVersion = run.LifecycleVersion + 1,
            Status = status,
            UpdatedAtUtc = now,
            CompletedAtUtc = isTerminal ? now : null,
            ExecutionClock = clock,
            Events = [.. run.Events, lifecycle],
            FinalOutput = null,
            FailureCode = status is CustomLoopRunStatus.Failed or CustomLoopRunStatus.NeedsReview ? "lifecycle_control_failed" : null,
            FailureDetail = status is CustomLoopRunStatus.Failed or CustomLoopRunStatus.NeedsReview ? detail : null
        };
    }

    private static CustomLoopExecutionClock StopClock(CustomLoopExecutionClock clock, DateTimeOffset now)
    {
        var accumulated = clock.AccumulatedRunningMilliseconds;
        if (clock.ActiveSinceUtc is { } activeSince)
        {
            accumulated = checked(accumulated + Math.Max(0, (long)(now - activeSince).TotalMilliseconds));
        }

        return new CustomLoopExecutionClock(Math.Min(accumulated, CustomLoopLimits.MaxRunExecutionMilliseconds), null);
    }

    private async Task<CustomLoopRunRecord?> TryLoadAsync(string runId, CancellationToken cancellationToken)
    {
        try
        {
            return await _runStore.GetAsync(runId, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private static CustomLoopControlKind KindForTransition(CustomLoopRunStatus current, CustomLoopRunStatus next)
    {
        if (next is CustomLoopRunStatus.PauseRequested or CustomLoopRunStatus.Paused || current == CustomLoopRunStatus.PauseRequested && next == CustomLoopRunStatus.Completed)
        {
            return CustomLoopControlKind.Pause;
        }

        return next is CustomLoopRunStatus.CancelRequested or CustomLoopRunStatus.Cancelled ? CustomLoopControlKind.Cancel : CustomLoopControlKind.Resume;
    }

    private static CustomLoopControlStatus StatusFor(CustomLoopRunStatus status, CustomLoopControlKind kind)
    {
        return status switch
        {
            CustomLoopRunStatus.PauseRequested => CustomLoopControlStatus.PauseRequested,
            CustomLoopRunStatus.Paused => CustomLoopControlStatus.Paused,
            CustomLoopRunStatus.CancelRequested => CustomLoopControlStatus.CancelRequested,
            CustomLoopRunStatus.Cancelled => CustomLoopControlStatus.Cancelled,
            CustomLoopRunStatus.Completed => CustomLoopControlStatus.Completed,
            CustomLoopRunStatus.Failed => CustomLoopControlStatus.Failed,
            CustomLoopRunStatus.NeedsReview => CustomLoopControlStatus.NeedsReview,
            CustomLoopRunStatus.Running when kind == CustomLoopControlKind.Resume => CustomLoopControlStatus.Resumed,
            _ => CustomLoopControlStatus.InvalidState
        };
    }

    private static void ValidateRequest(string runId, int expectedLifecycleVersion, string operationId, string actor)
    {
        CustomLoopArtifactIdentifier.Require(runId, nameof(runId));
        CustomLoopArtifactIdentifier.Require(operationId, nameof(operationId), CustomLoopLimits.MaxMutationOperationIdCharacters);
        if (expectedLifecycleVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedLifecycleVersion), "Expected lifecycle version must be at least one.");
        }

        if (string.IsNullOrWhiteSpace(actor) || actor.Length > CustomLoopLimits.MaxTraceReferenceCharacters || !actor.IsNormalized(NormalizationForm.FormC) || actor.Any(character => char.IsControl(character) || char.IsSurrogate(character)))
        {
            throw new ArgumentException("Actor must be bounded normalized text without control or invalid surrogate characters.", nameof(actor));
        }
    }

    private DateTimeOffset Now(CustomLoopRunRecord run)
    {
        return UtcNow(run.UpdatedAtUtc);
    }

    private DateTimeOffset UtcNow(DateTimeOffset? minimum = null)
    {
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        return minimum is { } value && now < value ? value : now;
    }

    private static string NewEventId(string prefix)
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

    private static CustomLoopControlResult Result(CustomLoopControlStatus status, CustomLoopRunRecord? run, string operationId, string detail)
    {
        return new CustomLoopControlResult(status, run, operationId, detail);
    }

    private sealed record TransitionResult(CustomLoopRunRecord? Run, CustomLoopRunRecord? CurrentRun, CustomLoopControlStatus Status, bool AuditRecorded, string Detail);
}
