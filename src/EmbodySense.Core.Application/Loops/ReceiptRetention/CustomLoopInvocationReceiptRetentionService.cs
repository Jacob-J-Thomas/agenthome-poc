using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Loops.ReceiptRetention.Models;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Governance.Audit.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;

namespace EmbodySense.Core.Application.Loops.ReceiptRetention;

public sealed class CustomLoopInvocationReceiptRetentionService
{
    private const int MaxCandidateReselections = 1;
    private readonly ICustomLoopInvocationOperationStore _store;
    private readonly IAuditLog _auditLog;
    private readonly TimeProvider _timeProvider;

    public CustomLoopInvocationReceiptRetentionService(ICustomLoopInvocationOperationStore store, IAuditLog auditLog, TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<CustomLoopInvocationReceiptRetentionResult> PruneForCapacityAsync(string actor, string surface, CancellationToken cancellationToken = default)
    {
        return PruneForCapacityAsync(actor, surface, MaxCandidateReselections, cancellationToken);
    }

    private async Task<CustomLoopInvocationReceiptRetentionResult> PruneForCapacityAsync(string actor, string surface, int remainingCandidateReselections, CancellationToken cancellationToken)
    {
        if (!IsBoundedText(actor, CustomLoopLimits.MaxTraceReferenceCharacters) || !CustomLoopArtifactIdentifier.IsValid(surface))
        {
            return Result(CustomLoopInvocationReceiptRetentionStatus.Invalid, null, "Receipt retention requires a canonical actor and surface.");
        }

        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var request = new CustomLoopInvocationReceiptRetentionRequest(
            $"receipt-retention-{Guid.NewGuid():N}",
            actor,
            surface,
            now,
            now - CustomLoopInvocationReceiptRetentionPolicy.MinimumReplayDuration);
        CustomLoopInvocationReceiptRetentionReservationResult reserved;
        try
        {
            reserved = await _store.ReserveCompletedReceiptRetentionAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Result(CustomLoopInvocationReceiptRetentionStatus.Invalid, null, $"Completed invocation receipts could not be inspected safely: {exception.GetType().Name}.");
        }

        if (reserved.Status == CustomLoopInvocationReceiptRetentionReservationStatus.NothingEligible)
        {
            return Result(CustomLoopInvocationReceiptRetentionStatus.NothingEligible, null, $"No completed invocation receipt is older than the {CustomLoopInvocationReceiptRetentionPolicy.MinimumReplayDuration.TotalDays:0}-day replay boundary; pending and newer receipts were preserved.");
        }

        if (reserved.Status == CustomLoopInvocationReceiptRetentionReservationStatus.OperationInProgress)
        {
            return Result(CustomLoopInvocationReceiptRetentionStatus.OperationInProgress, reserved.Operation, "Another process is inside the bounded receipt-retention intent or mutation window.");
        }

        var operation = reserved.Operation;
        if (operation is null)
        {
            return Result(CustomLoopInvocationReceiptRetentionStatus.Invalid, null, "Receipt retention did not retain its durable operation journal.");
        }

        using var ownerWindow = CreateOwnerWindow(operation.OwnershipStartedAtUtc, cancellationToken);
        var ownerToken = ownerWindow.Token;
        var isReplay = reserved.Status != CustomLoopInvocationReceiptRetentionReservationStatus.Reserved;
        if (reserved.Status == CustomLoopInvocationReceiptRetentionReservationStatus.Reserved)
        {
            if (!await TryAppendAuditAsync(CreateAudit(AuditSchema.Actions.LoopInvocationReceiptRetentionIntent, AuditSchema.Outcomes.Requested, operation, "Expired completed invocation receipts were selected for governed retention cleanup."), ownerToken, cancellationToken))
            {
                return Result(CustomLoopInvocationReceiptRetentionStatus.AuditUnavailable, operation, "No receipt was deleted because the retention-intent audit could not be recorded.");
            }

            try
            {
                operation = await _store.MarkReceiptRetentionIntentAuditedAsync(operation.OperationId, UtcNow(operation.UpdatedAtUtc), ownerToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    operation = await _store.CommitCompletedReceiptRetentionAsync(operation.OperationId, UtcNow(operation.UpdatedAtUtc), ownerToken);
                }
                catch (Exception)
                {
                    return Result(CustomLoopInvocationReceiptRetentionStatus.OperationInProgress, operation, $"The retention intent may be durable, but its bounded owner could not advance the operation journal safely: {exception.GetType().Name}.");
                }
            }
        }

        if (operation.State == CustomLoopInvocationReceiptRetentionOperationState.IntentAuditRecorded)
        {
            try
            {
                operation = await _store.CommitCompletedReceiptRetentionAsync(operation.OperationId, UtcNow(operation.UpdatedAtUtc), ownerToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var recovered = await _store.ReserveCompletedReceiptRetentionAsync(request, cancellationToken);
                    if (recovered.Status == CustomLoopInvocationReceiptRetentionReservationStatus.OperationInProgress)
                    {
                        return Result(CustomLoopInvocationReceiptRetentionStatus.OperationInProgress, recovered.Operation ?? operation, "The bounded retention owner is still committing or reconciling the selected receipt batch.");
                    }

                    if (recovered.Status != CustomLoopInvocationReceiptRetentionReservationStatus.OutcomeCommitted || recovered.Operation is null)
                    {
                        return Result(CustomLoopInvocationReceiptRetentionStatus.Invalid, operation, $"Receipt retention could not be committed or reconciled safely: {exception.GetType().Name}.");
                    }

                    operation = recovered.Operation;
                    isReplay = true;
                }
                catch (Exception)
                {
                    return Result(CustomLoopInvocationReceiptRetentionStatus.Invalid, operation, $"Receipt retention could not be committed or reconciled safely: {exception.GetType().Name}.");
                }
            }
        }

        if (operation.State == CustomLoopInvocationReceiptRetentionOperationState.AbandonedCandidateChanged)
        {
            try
            {
                operation = await _store.MarkReceiptRetentionConflictAuditStartedAsync(operation.OperationId, UtcNow(operation.UpdatedAtUtc), ownerToken);
            }
            catch (Exception)
            {
                return Result(CustomLoopInvocationReceiptRetentionStatus.OperationInProgress, operation, "A changed or unexplained missing receipt was preserved, but its conflict audit could not be durably started.");
            }
        }

        if (operation.State == CustomLoopInvocationReceiptRetentionOperationState.AbandonedConflictAuditStarted)
        {
            if (!await TryAppendAuditAsync(CreateAudit(AuditSchema.Actions.LoopInvocationReceiptRetentionOutcome, AuditSchema.Outcomes.Conflict, operation, "No changed or unexplained missing receipt was attributed to cleanup; the immutable batch was abandoned for safe reselection."), ownerToken, cancellationToken))
            {
                try
                {
                    operation = await _store.MarkReceiptRetentionConflictAuditWarningAsync(operation.OperationId, UtcNow(operation.UpdatedAtUtc), ownerToken);
                }
                catch (Exception)
                {
                }

                return Result(CustomLoopInvocationReceiptRetentionStatus.AuditUnavailable, operation, "A changed or unexplained missing receipt was preserved, but its conflict outcome audit could not be recorded.");
            }

            try
            {
                operation = await _store.MarkReceiptRetentionConflictAuditedAsync(operation.OperationId, UtcNow(operation.UpdatedAtUtc), ownerToken);
            }
            catch (Exception)
            {
                return Result(CustomLoopInvocationReceiptRetentionStatus.CommittedWithAuditWarning, operation, "The retention conflict audit was appended, but its durable completion marker requires review and the batch will not be replaced.");
            }
        }

        if (operation.State == CustomLoopInvocationReceiptRetentionOperationState.AbandonedConflictAuditRecorded)
        {
            if (remainingCandidateReselections == 0)
            {
                return Result(CustomLoopInvocationReceiptRetentionStatus.OperationInProgress, operation, "A selected receipt changed or disappeared again during cleanup; nothing was attributed to this batch and a later request may safely reselect.");
            }

            return await PruneForCapacityAsync(actor, surface, remainingCandidateReselections - 1, cancellationToken);
        }

        if (operation.State == CustomLoopInvocationReceiptRetentionOperationState.AbandonedWithAuditWarning)
        {
            return Result(CustomLoopInvocationReceiptRetentionStatus.CommittedWithAuditWarning, operation, "A changed or unexplained missing receipt was preserved, but the bounded conflict-audit attempt requires review and the journal was retained.");
        }

        if (operation.State == CustomLoopInvocationReceiptRetentionOperationState.CommittedWithAuditWarning)
        {
            return Result(CustomLoopInvocationReceiptRetentionStatus.CommittedWithAuditWarning, operation, "Expired completed invocation receipts were deleted, but the original bounded outcome-audit attempt requires review and was not duplicated.");
        }

        if (operation.State == CustomLoopInvocationReceiptRetentionOperationState.OutcomeAuditRecorded)
        {
            return Result(CustomLoopInvocationReceiptRetentionStatus.Replayed, operation, "The completed-receipt retention operation and its audits were already committed.");
        }

        if (operation.State == CustomLoopInvocationReceiptRetentionOperationState.OutcomeAuditStarted)
        {
            try
            {
                operation = await _store.MarkReceiptRetentionOutcomeAuditWarningAsync(operation.OperationId, UtcNow(operation.UpdatedAtUtc), ownerToken);
            }
            catch (Exception)
            {
                return Result(CustomLoopInvocationReceiptRetentionStatus.CommittedWithAuditWarning, operation, "Expired completed invocation receipts were deleted; an interrupted outcome-audit attempt and its warning marker require review.");
            }

            return Result(CustomLoopInvocationReceiptRetentionStatus.CommittedWithAuditWarning, operation, "Expired completed invocation receipts were deleted; the interrupted outcome audit was not duplicated.");
        }

        if (operation.State != CustomLoopInvocationReceiptRetentionOperationState.OutcomeCommitted)
        {
            return Result(CustomLoopInvocationReceiptRetentionStatus.Invalid, operation, "Receipt retention stopped in an unsupported durable operation state.");
        }

        try
        {
            operation = await _store.MarkReceiptRetentionOutcomeAuditStartedAsync(operation.OperationId, UtcNow(operation.UpdatedAtUtc), ownerToken);
        }
        catch (Exception)
        {
            return Result(CustomLoopInvocationReceiptRetentionStatus.CommittedWithAuditWarning, operation, "Expired completed invocation receipts were deleted, but the bounded outcome-audit attempt could not be durably started.");
        }

        if (!await TryAppendAuditAsync(CreateAudit(AuditSchema.Actions.LoopInvocationReceiptRetentionOutcome, AuditSchema.Outcomes.Succeeded, operation, "Expired completed invocation receipts were deleted after the replay boundary."), ownerToken, cancellationToken))
        {
            try
            {
                operation = await _store.MarkReceiptRetentionOutcomeAuditWarningAsync(operation.OperationId, UtcNow(operation.UpdatedAtUtc), ownerToken);
            }
            catch (Exception)
            {
            }

            return Result(CustomLoopInvocationReceiptRetentionStatus.CommittedWithAuditWarning, operation, "Expired completed invocation receipts were deleted, but the bounded outcome-audit attempt failed and will not be duplicated.");
        }

        try
        {
            operation = await _store.MarkReceiptRetentionOutcomeAuditedAsync(operation.OperationId, UtcNow(operation.UpdatedAtUtc), ownerToken);
        }
        catch (Exception exception)
        {
            return Result(CustomLoopInvocationReceiptRetentionStatus.CommittedWithAuditWarning, operation, $"Expired completed invocation receipts were deleted and the outcome audit was appended, but its durable completion marker failed: {exception.GetType().Name}.");
        }

        return Result(isReplay ? CustomLoopInvocationReceiptRetentionStatus.Replayed : CustomLoopInvocationReceiptRetentionStatus.Pruned, operation, $"Deleted {operation.DeletedReceiptCount} expired completed invocation receipt(s) after the {CustomLoopInvocationReceiptRetentionPolicy.MinimumReplayDuration.TotalDays:0}-day replay boundary.");
    }

    private DateTimeOffset UtcNow(DateTimeOffset minimum)
    {
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        return now < minimum ? minimum : now;
    }

    private CancellationTokenSource CreateOwnerWindow(DateTimeOffset ownershipStartedAtUtc, CancellationToken cancellationToken)
    {
        var ownerWindow = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var remaining = ownershipStartedAtUtc + CustomLoopInvocationReceiptRetentionPolicy.OperationOwnershipWindow - now;
        ownerWindow.CancelAfter(remaining <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : remaining);
        return ownerWindow;
    }

    private async Task<bool> TryAppendAuditAsync(AuditEvent auditEvent, CancellationToken ownerToken, CancellationToken callerToken)
    {
        try
        {
            await _auditLog.AppendAsync(auditEvent, ownerToken);
            return true;
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    private static AuditEvent CreateAudit(string action, string outcome, CustomLoopInvocationReceiptRetentionOperation operation, string detail)
    {
        return AuditEvent.Create(
            operation.Actor,
            action,
            operation.OperationId,
            outcome,
            detail,
            new Dictionary<string, object?>
            {
                ["operation_id"] = operation.OperationId,
                ["surface"] = operation.Surface,
                ["minimum_replay_days"] = CustomLoopInvocationReceiptRetentionPolicy.MinimumReplayDuration.TotalDays,
                ["replay_cutoff_utc"] = operation.ReplayCutoffUtc,
                ["ownership_started_at_utc"] = operation.OwnershipStartedAtUtc,
                ["selected_receipt_count"] = operation.Candidates.Length,
                ["selected_receipt_utf8_bytes"] = operation.Candidates.Sum(candidate => candidate.ArtifactUtf8Bytes),
                ["deleted_receipt_count"] = operation.DeletedReceiptCount,
                ["deleted_receipt_utf8_bytes"] = operation.DeletedReceiptUtf8Bytes
            });
    }

    private static CustomLoopInvocationReceiptRetentionResult Result(CustomLoopInvocationReceiptRetentionStatus status, CustomLoopInvocationReceiptRetentionOperation? operation, string detail)
    {
        return new CustomLoopInvocationReceiptRetentionResult(status, operation?.DeletedReceiptCount ?? 0, operation?.DeletedReceiptUtf8Bytes ?? 0, detail);
    }

    private static bool IsBoundedText(string? value, int maximumLength)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= maximumLength
            && !value.Any(character => char.IsControl(character) || char.IsSurrogate(character));
    }
}
