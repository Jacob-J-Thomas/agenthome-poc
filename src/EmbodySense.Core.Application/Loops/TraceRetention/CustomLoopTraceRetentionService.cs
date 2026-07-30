using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Application.Loops.TraceRetention.Models;
using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Loops.Models.Custom;

namespace EmbodySense.Core.Application.Loops.TraceRetention;

/// <summary>
/// Inspects quota evidence and performs authenticated, hash-bound deletion of terminal custom-loop traces.
/// </summary>
public sealed class CustomLoopTraceRetentionService
{
    private static readonly TimeSpan _integrityWriteTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _reservationOwnershipTimeout = _integrityWriteTimeout + TimeSpan.FromSeconds(5);
    private readonly ICustomLoopRunStore _store;
    private readonly IAuditLog _auditLog;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="CustomLoopTraceRetentionService"/> type.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="auditLog">The audit log.</param>
    /// <param name="timeProvider">The time provider.</param>
    public CustomLoopTraceRetentionService(ICustomLoopRunStore store, IAuditLog auditLog, TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Inspects retained artifacts and computes the deletion evidence hash for one run.
    /// </summary>
    /// <param name="runId">The run ID.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The inspection, or <see langword="null"/> when the run is unknown.</returns>
    public Task<CustomLoopTraceInspection?> InspectAsync(string runId, CancellationToken cancellationToken = default) => _store.InspectTraceAsync(runId, cancellationToken);

    /// <summary>
    /// Reads current trace usage and configured limits.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result is the custom loop trace quota.</returns>
    public Task<CustomLoopTraceQuota> GetQuotaAsync(CancellationToken cancellationToken = default) => _store.GetTraceQuotaAsync(cancellationToken);

    /// <summary>
    /// Deletes a terminal trace through an idempotent, audited, expected-hash-bound operation.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The committed or replayed tombstone, or a bounded rejection/conflict result.</returns>
    public async Task<CustomLoopTraceDeletionResult> DeleteAsync(CustomLoopTraceDeletionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validationDetail = Validate(request);
        if (validationDetail is not null)
        {
            return Result(CustomLoopTraceDeletionStatus.Invalid, null, validationDetail);
        }

        // The operation receipt binds actor, run, surface, and expected hash. Reusing its id for any
        // other authenticated request is an explicit conflict.
        var requestHash = CustomLoopTraceDeletionRequestHash.Compute(request);
        var mutation = new CustomLoopTraceDeletionMutation(request, requestHash, _timeProvider.GetUtcNow().ToUniversalTime());
        CustomLoopTraceDeletionLookupResult lookup;
        try
        {
            lookup = await _store.GetTraceDeletionOperationAsync(request.OperationId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Result(CustomLoopTraceDeletionStatus.Invalid, null, $"The deletion operation ledger could not be read safely: {exception.GetType().Name}.");
        }

        if (lookup.Operation is not null)
        {
            if (!OperationMatches(lookup.Operation, request, requestHash))
            {
                return Result(CustomLoopTraceDeletionStatus.Conflict, lookup.Operation.Tombstone, "The deletion operation id was reused for a different authenticated request.");
            }

            if (lookup.Status == CustomLoopTraceDeletionLookupStatus.OutcomeCommitted)
            {
                return await CompleteOutcomeAsync(request, lookup.Operation.ToStoreResult(), isReplay: true, lookup.Operation.UpdatedAtUtc);
            }

            if (lookup.Status == CustomLoopTraceDeletionLookupStatus.PendingMutation)
            {
                return await RecoverPendingReservationAsync(mutation, lookup.Operation);
            }

            return Result(CustomLoopTraceDeletionStatus.Invalid, null, "The deletion operation ledger contains an unsupported state.");
        }

        CustomLoopTraceInspection? inspection;
        try
        {
            inspection = await _store.InspectTraceAsync(request.RunId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Result(CustomLoopTraceDeletionStatus.Invalid, null, $"The trace could not be inspected safely: {exception.GetType().Name}.");
        }

        if (inspection is null)
        {
            return Result(CustomLoopTraceDeletionStatus.NotFound, null, "The run trace does not exist.");
        }

        if (inspection.IsDeleted)
        {
            return Result(CustomLoopTraceDeletionStatus.Conflict, inspection.Tombstone, "The terminal trace was already deleted by a different confirmed operation.");
        }

        if (inspection.CompletedAtUtc is null)
        {
            return Result(CustomLoopTraceDeletionStatus.Nonterminal, null, "Only terminal run traces can be deleted.");
        }

        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(inspection.PersistedArtifactHash), Encoding.ASCII.GetBytes(request.ExpectedTraceHash)))
        {
            return Result(CustomLoopTraceDeletionStatus.HashMismatch, null, "The persisted trace changed; inspect it again before deleting sensitive content.");
        }

        CustomLoopTraceDeletionReservationResult reservation;
        try
        {
            reservation = await _store.ReserveTraceDeletionOperationAsync(mutation, cancellationToken);
        }
        catch (UnsupportedCustomLoopRunDiscoveryIndexSchemaException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            try
            {
                using var recoveryWindow = new CancellationTokenSource(_integrityWriteTimeout);
                var recovered = await _store.GetTraceDeletionOperationAsync(request.OperationId, recoveryWindow.Token);
                if (recovered.Operation is not null && OperationMatches(recovered.Operation, request, requestHash))
                {
                    return recovered.Status == CustomLoopTraceDeletionLookupStatus.OutcomeCommitted
                        ? await CompleteOutcomeAsync(request, recovered.Operation.ToStoreResult(), isReplay: true, recovered.Operation.UpdatedAtUtc)
                        : await RecoverPendingReservationAsync(mutation, recovered.Operation);
                }
            }
            catch (Exception)
            {
            }

            return Result(CustomLoopTraceDeletionStatus.Invalid, null, $"The deletion operation could not be reserved safely: {exception.GetType().Name}.");
        }

        if (reservation.Status == CustomLoopTraceDeletionReservationStatus.OperationConflict)
        {
            return Result(CustomLoopTraceDeletionStatus.Conflict, reservation.Operation?.Tombstone, "The deletion operation id was reused for a different authenticated request.");
        }

        if (reservation.Status == CustomLoopTraceDeletionReservationStatus.DeletionOperationLimitExceeded)
        {
            return Result(CustomLoopTraceDeletionStatus.OperationLimitExceeded, null, "The explicit trace-deletion operation receipt limit was reached; no trace content was deleted.");
        }

        if (reservation.Operation is null)
        {
            return Result(CustomLoopTraceDeletionStatus.Invalid, null, "The deletion operation reservation did not retain its durable receipt.");
        }

        if (reservation.Status == CustomLoopTraceDeletionReservationStatus.OutcomeCommitted)
        {
            return await CompleteOutcomeAsync(request, reservation.Operation.ToStoreResult(), isReplay: true, reservation.Operation.UpdatedAtUtc);
        }

        if (reservation.Status == CustomLoopTraceDeletionReservationStatus.Pending)
        {
            return await RecoverPendingReservationAsync(mutation, reservation.Operation);
        }

        if (reservation.Status != CustomLoopTraceDeletionReservationStatus.Reserved)
        {
            return Result(CustomLoopTraceDeletionStatus.Invalid, null, "The deletion operation reservation returned an unsupported state.");
        }

        using var ownerWindow = CreateOwnerWindow(reservation.Operation.UpdatedAtUtc, cancellationToken);
        if (!await TryAppendAuditAsync(CreateAudit(AuditSchema.Actions.LoopTraceDeletionIntent, AuditSchema.Outcomes.Requested, request, inspection, null), ownerWindow.Token))
        {
            return await CommitAuditFailureAsync(mutation, "The trace was not changed because its deletion-intent audit could not be recorded.");
        }

        CustomLoopTraceDeletionStoreResult stored;
        try
        {
            stored = await _store.DeleteTerminalTraceAsync(mutation, ownerWindow.Token);
        }
        catch (UnsupportedCustomLoopRunDiscoveryIndexSchemaException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var recovered = await TryRecoverCommittedDeletionAsync(mutation);
            if (recovered is not null)
            {
                return recovered;
            }

            return Result(CustomLoopTraceDeletionStatus.Invalid, null, $"The trace deletion could not be persisted safely: {exception.GetType().Name}.");
        }

        if (!stored.HasCommittedOutcome)
        {
            return MapRejectedStoreResult(stored);
        }

        return await CompleteOutcomeAsync(request, stored, isReplay: false);
    }

    private async Task<CustomLoopTraceDeletionResult> RecoverPendingReservationAsync(CustomLoopTraceDeletionMutation mutation, CustomLoopTraceDeletionOperation operation)
    {
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        if (operation.UpdatedAtUtc > now - _reservationOwnershipTimeout)
        {
            return Result(CustomLoopTraceDeletionStatus.OperationInProgress, null, "The matching deletion operation is still inside its bounded end-to-end intent/mutation ownership window.");
        }

        var recovered = await TryRecoverCommittedDeletionAsync(mutation);
        if (recovered is not null)
        {
            return recovered;
        }

        return await CommitAuditFailureAsync(mutation, "The trace was not changed because a prior deletion owner ended before its intent and mutation could be reconciled safely.");
    }

    private async Task<CustomLoopTraceDeletionResult?> TryRecoverCommittedDeletionAsync(CustomLoopTraceDeletionMutation mutation)
    {
        using var recoveryWindow = new CancellationTokenSource(_integrityWriteTimeout);
        try
        {
            var lookup = await _store.GetTraceDeletionOperationAsync(mutation.Request.OperationId, recoveryWindow.Token);
            if (lookup.Operation is not null && lookup.Status == CustomLoopTraceDeletionLookupStatus.OutcomeCommitted)
            {
                return await CompleteOutcomeAsync(mutation.Request, lookup.Operation.ToStoreResult(), isReplay: true, lookup.Operation.UpdatedAtUtc);
            }

            var inspection = await _store.InspectTraceAsync(mutation.Request.RunId, recoveryWindow.Token);
            if (inspection?.Tombstone is null
                || !string.Equals(inspection.Tombstone.DeletionOperationId, mutation.Request.OperationId, StringComparison.Ordinal)
                || !string.Equals(inspection.Tombstone.DeletionRequestHash, mutation.RequestHash, StringComparison.Ordinal))
            {
                return null;
            }

            try
            {
                var reconciled = await _store.DeleteTerminalTraceAsync(mutation, recoveryWindow.Token);
                return reconciled.HasCommittedOutcome
                    ? await CompleteOutcomeAsync(mutation.Request, reconciled, isReplay: true)
                    : Result(CustomLoopTraceDeletionStatus.CommittedWithAuditWarning, inspection.Tombstone, "The trace deletion committed, but its durable operation outcome remains pending reconciliation.");
            }
            catch (Exception)
            {
                return Result(CustomLoopTraceDeletionStatus.CommittedWithAuditWarning, inspection.Tombstone, "The trace deletion committed, but its durable operation outcome remains pending reconciliation.");
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<CustomLoopTraceDeletionResult> CommitAuditFailureAsync(CustomLoopTraceDeletionMutation mutation, string detail)
    {
        using var integrityWindow = new CancellationTokenSource(_integrityWriteTimeout);
        try
        {
            var stored = await _store.CommitTraceDeletionAuditFailureAsync(mutation, integrityWindow.Token);
            if (!stored.HasCommittedOutcome)
            {
                return Result(CustomLoopTraceDeletionStatus.AuditUnavailable, null, detail + " Its durable rejection outcome could not be completed.");
            }

            return await CompleteOutcomeAsync(mutation.Request, stored, isReplay: false);
        }
        catch (Exception)
        {
            return Result(CustomLoopTraceDeletionStatus.AuditUnavailable, null, detail + " Its durable rejection outcome requires recovery.");
        }
    }

    private async Task<CustomLoopTraceDeletionResult> CompleteOutcomeAsync(CustomLoopTraceDeletionRequest request, CustomLoopTraceDeletionStoreResult stored, bool isReplay, DateTimeOffset? outcomeAuditStartedAtUtc = null)
    {
        if (stored.IsCommitted && stored.Tombstone is null)
        {
            return Result(CustomLoopTraceDeletionStatus.Invalid, null, "The committed trace deletion did not retain its required tombstone.", outcomeCommitted: true);
        }

        if (stored.IsCommitted && stored.Integrity == CustomLoopTraceDeletionIntegrity.Unknown)
        {
            return Result(CustomLoopTraceDeletionStatus.CommittedWithAuditWarning, stored.Tombstone, "The trace deletion is committed, but its durable audit-integrity state requires review.", outcomeCommitted: true);
        }

        if (!stored.HasCommittedOutcome)
        {
            return MapRejectedStoreResult(stored);
        }

        if (stored.Integrity == CustomLoopTraceDeletionIntegrity.Complete)
        {
            return ProjectCompletedOutcome(stored, isReplay, auditWarning: false);
        }

        if (stored.Integrity == CustomLoopTraceDeletionIntegrity.CommittedWithAuditWarning)
        {
            return ProjectCompletedOutcome(stored, isReplay, auditWarning: true);
        }

        if (stored.Integrity == CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted)
        {
            return await ResolveInterruptedOutcomeAuditAsync(request, stored, outcomeAuditStartedAtUtc);
        }

        if (stored.Integrity != CustomLoopTraceDeletionIntegrity.PendingOutcomeAudit)
        {
            return stored.IsCommitted
                ? Result(CustomLoopTraceDeletionStatus.CommittedWithAuditWarning, stored.Tombstone, "The trace deletion is committed, but its durable audit-integrity state requires review.", outcomeCommitted: true)
                : Result(CustomLoopTraceDeletionStatus.Invalid, stored.Tombstone, "The trace-deletion rejection has an unsupported durable audit-integrity state.");
        }

        using var integrityWindow = new CancellationTokenSource(_integrityWriteTimeout);
        try
        {
            var started = await _store.MarkTraceDeletionOutcomeAsync(request.OperationId, CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted, integrityWindow.Token);
            if (started == CustomLoopTraceDeletionAuditMarkStatus.AlreadyMarked)
            {
                var existing = await _store.GetTraceDeletionOperationAsync(request.OperationId, integrityWindow.Token);
                if (existing.Operation?.Integrity == CustomLoopTraceDeletionIntegrity.Complete)
                {
                    return ProjectCompletedOutcome(existing.Operation.ToStoreResult(), isReplay: true, auditWarning: false);
                }

                if (existing.Operation?.Integrity == CustomLoopTraceDeletionIntegrity.CommittedWithAuditWarning)
                {
                    return ProjectCompletedOutcome(existing.Operation.ToStoreResult(), isReplay: true, auditWarning: true);
                }

                var existingStored = existing.Operation?.ToStoreResult() ?? stored;
                return await ResolveInterruptedOutcomeAuditAsync(request, existingStored, existing.Operation?.UpdatedAtUtc);
            }

            if (started != CustomLoopTraceDeletionAuditMarkStatus.Marked)
            {
                return ProjectCompletedOutcome(stored with { Integrity = CustomLoopTraceDeletionIntegrity.CommittedWithAuditWarning }, isReplay, auditWarning: true);
            }
        }
        catch (Exception)
        {
            return ProjectCompletedOutcome(stored with { Integrity = CustomLoopTraceDeletionIntegrity.CommittedWithAuditWarning }, isReplay, auditWarning: true);
        }

        var audited = await TryAppendAuditAsync(CreateAudit(AuditSchema.Actions.LoopTraceDeletionOutcome, AuditOutcome(stored.Status), request, null, stored.Tombstone), integrityWindow.Token);
        var desiredIntegrity = audited ? CustomLoopTraceDeletionIntegrity.Complete : CustomLoopTraceDeletionIntegrity.CommittedWithAuditWarning;
        try
        {
            var mark = await _store.MarkTraceDeletionOutcomeAsync(request.OperationId, desiredIntegrity, integrityWindow.Token);
            if (mark is not CustomLoopTraceDeletionAuditMarkStatus.Marked and not CustomLoopTraceDeletionAuditMarkStatus.AlreadyMarked)
            {
                return ProjectCompletedOutcome(stored with { Integrity = CustomLoopTraceDeletionIntegrity.CommittedWithAuditWarning }, isReplay, auditWarning: true);
            }

            var refreshed = await _store.GetTraceDeletionOperationAsync(request.OperationId, integrityWindow.Token);
            if (refreshed.Operation is not null)
            {
                return ProjectCompletedOutcome(refreshed.Operation.ToStoreResult(), isReplay, refreshed.Operation.Integrity != CustomLoopTraceDeletionIntegrity.Complete);
            }

            return ProjectCompletedOutcome(stored with { Integrity = desiredIntegrity }, isReplay, !audited);
        }
        catch (Exception)
        {
            return ProjectCompletedOutcome(stored with { Integrity = CustomLoopTraceDeletionIntegrity.CommittedWithAuditWarning }, isReplay, auditWarning: true);
        }
    }

    private async Task<CustomLoopTraceDeletionResult> ResolveInterruptedOutcomeAuditAsync(CustomLoopTraceDeletionRequest request, CustomLoopTraceDeletionStoreResult stored, DateTimeOffset? outcomeAuditStartedAtUtc)
    {
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        if (outcomeAuditStartedAtUtc is not null && outcomeAuditStartedAtUtc.Value <= now && outcomeAuditStartedAtUtc.Value > now - _integrityWriteTimeout)
        {
            return ProjectCompletedOutcome(stored, isReplay: true, auditWarning: true, "The existing outcome-audit owner is still active; retry after that bounded integrity window completes.");
        }

        using var integrityWindow = new CancellationTokenSource(_integrityWriteTimeout);
        try
        {
            await _store.MarkTraceDeletionOutcomeAsync(request.OperationId, CustomLoopTraceDeletionIntegrity.CommittedWithAuditWarning, integrityWindow.Token);
            var refreshed = await _store.GetTraceDeletionOperationAsync(request.OperationId, integrityWindow.Token);
            if (refreshed.Operation?.Integrity == CustomLoopTraceDeletionIntegrity.Complete)
            {
                return ProjectCompletedOutcome(refreshed.Operation.ToStoreResult(), isReplay: true, auditWarning: false);
            }

            var warning = refreshed.Operation?.ToStoreResult() ?? stored with { Integrity = CustomLoopTraceDeletionIntegrity.CommittedWithAuditWarning };
            return ProjectCompletedOutcome(warning, isReplay: true, auditWarning: true, "A prior outcome-audit attempt was interrupted, so audit integrity requires review and the audit was not duplicated.");
        }
        catch (Exception)
        {
            return ProjectCompletedOutcome(stored with { Integrity = CustomLoopTraceDeletionIntegrity.CommittedWithAuditWarning }, isReplay: true, auditWarning: true, "An interrupted outcome-audit attempt and its incomplete warning marker require review.");
        }
    }

    private static CustomLoopTraceDeletionResult ProjectCompletedOutcome(CustomLoopTraceDeletionStoreResult stored, bool isReplay, bool auditWarning, string? detail = null)
    {
        if (!stored.IsCommitted)
        {
            var rejected = MapRejectedStoreResult(stored);
            var rejectionDetail = detail ?? (auditWarning ? rejected.Detail + " Its terminal outcome audit requires review." : rejected.Detail);
            return rejected with { Detail = rejectionDetail, IsOutcomeCommitted = true };
        }

        if (auditWarning)
        {
            return Result(CustomLoopTraceDeletionStatus.CommittedWithAuditWarning, stored.Tombstone, detail ?? "The trace deletion is committed; its outcome-audit warning remains visible.", outcomeCommitted: true);
        }

        return Result(
            isReplay ? CustomLoopTraceDeletionStatus.Replayed : CustomLoopTraceDeletionStatus.Deleted,
            stored.Tombstone,
            detail ?? (isReplay ? "The confirmed trace deletion was already committed and fully audited." : "The terminal trace content was replaced by an audited tombstone."),
            outcomeCommitted: true);
    }

    private static string AuditOutcome(CustomLoopTraceDeletionStoreStatus status)
    {
        return status switch
        {
            CustomLoopTraceDeletionStoreStatus.Deleted or CustomLoopTraceDeletionStoreStatus.AlreadyDeleted => AuditSchema.Outcomes.Succeeded,
            CustomLoopTraceDeletionStoreStatus.NotFound => AuditSchema.Outcomes.NotFound,
            CustomLoopTraceDeletionStoreStatus.OperationConflict => AuditSchema.Outcomes.Conflict,
            CustomLoopTraceDeletionStoreStatus.AuditUnavailable => AuditSchema.Outcomes.Failed,
            _ => AuditSchema.Outcomes.Rejected
        };
    }

    private static CustomLoopTraceDeletionResult MapRejectedStoreResult(CustomLoopTraceDeletionStoreResult stored)
    {
        return stored.Status switch
        {
            CustomLoopTraceDeletionStoreStatus.NotFound => Result(CustomLoopTraceDeletionStatus.NotFound, null, "The run trace does not exist."),
            CustomLoopTraceDeletionStoreStatus.Nonterminal => Result(CustomLoopTraceDeletionStatus.Nonterminal, null, "Only terminal run traces can be deleted."),
            CustomLoopTraceDeletionStoreStatus.HashMismatch => Result(CustomLoopTraceDeletionStatus.HashMismatch, null, "The persisted trace changed; inspect it again before deleting sensitive content."),
            CustomLoopTraceDeletionStoreStatus.OperationConflict => Result(CustomLoopTraceDeletionStatus.Conflict, stored.Tombstone, "The deletion operation id was reused for a different authenticated request."),
            CustomLoopTraceDeletionStoreStatus.TombstoneLimitExceeded => Result(CustomLoopTraceDeletionStatus.LimitExceeded, null, "The explicit terminal-trace tombstone limit was reached; no trace content was deleted."),
            CustomLoopTraceDeletionStoreStatus.DeletionOperationLimitExceeded => Result(CustomLoopTraceDeletionStatus.OperationLimitExceeded, null, "The explicit trace-deletion operation receipt limit was reached; no trace content was deleted."),
            CustomLoopTraceDeletionStoreStatus.AuditUnavailable => Result(CustomLoopTraceDeletionStatus.AuditUnavailable, null, "The trace was not changed because its deletion-intent audit could not be established safely."),
            _ => Result(CustomLoopTraceDeletionStatus.Invalid, stored.Tombstone, $"The trace store rejected deletion with status `{stored.Status}`.")
        };
    }

    private static string? Validate(CustomLoopTraceDeletionRequest request)
    {
        if (!CustomLoopArtifactIdentifier.IsValid(request.RunId))
        {
            return "Run id must be a bounded safe artifact identifier.";
        }

        if (!IsHash(request.ExpectedTraceHash))
        {
            return "Expected trace hash must be lowercase SHA-256 hexadecimal.";
        }

        if (!CustomLoopArtifactIdentifier.IsValid(request.OperationId, CustomLoopLimits.MaxMutationOperationIdCharacters))
        {
            return "Deletion operation id must be a bounded safe identifier.";
        }

        if (!IsActor(request.Actor))
        {
            return "Deletion actor must be a bounded server-owned audit identifier.";
        }

        return IsSurface(request.Surface) ? null : "Deletion surface must be a normalized server-owned identifier.";
    }

    private static bool OperationMatches(CustomLoopTraceDeletionOperation operation, CustomLoopTraceDeletionRequest request, string requestHash)
    {
        return string.Equals(operation.OperationId, request.OperationId, StringComparison.Ordinal)
            && string.Equals(operation.RequestHash, requestHash, StringComparison.Ordinal)
            && operation.Request == request;
    }

    private async Task<bool> TryAppendAuditAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        try
        {
            await _auditLog.AppendAsync(auditEvent, cancellationToken);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private CancellationTokenSource CreateOwnerWindow(DateTimeOffset reservedAtUtc, CancellationToken cancellationToken)
    {
        var ownerWindow = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var remaining = reservedAtUtc + _integrityWriteTimeout - _timeProvider.GetUtcNow().ToUniversalTime();
        if (remaining <= TimeSpan.Zero)
        {
            ownerWindow.Cancel();
        }
        else
        {
            ownerWindow.CancelAfter(remaining < _integrityWriteTimeout ? remaining : _integrityWriteTimeout);
        }

        return ownerWindow;
    }

    private static AuditEvent CreateAudit(string action, string outcome, CustomLoopTraceDeletionRequest request, CustomLoopTraceInspection? inspection, CustomLoopTraceTombstone? tombstone)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["operation_id"] = request.OperationId,
            ["run_id"] = request.RunId,
            ["loop_id"] = inspection?.LoopId ?? tombstone?.LoopId,
            ["expected_trace_hash"] = request.ExpectedTraceHash,
            ["original_trace_utf8_bytes"] = inspection?.OriginalTraceUtf8Bytes ?? tombstone?.OriginalTraceUtf8Bytes,
            ["definition_version"] = inspection?.DefinitionVersion ?? tombstone?.DefinitionVersion,
            ["definition_hash"] = inspection?.DefinitionHash ?? tombstone?.DefinitionHash,
            ["terminal_status"] = (inspection?.TerminalStatus ?? tombstone?.TerminalStatus)?.ToString(),
            ["surface"] = request.Surface
        };
        return AuditEvent.Create(request.Actor, action, request.RunId, outcome, "Custom-loop terminal trace deletion metadata recorded.", metadata);
    }

    private static bool IsHash(string? value) => value is { Length: CustomLoopLimits.Sha256HexCharacters } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsActor(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= CustomLoopLimits.MaxArtifactIdCharacters && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '@' or ':');

    private static bool IsSurface(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= CustomLoopLimits.MaxArtifactIdCharacters && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static CustomLoopTraceDeletionResult Result(CustomLoopTraceDeletionStatus status, CustomLoopTraceTombstone? tombstone, string detail, bool outcomeCommitted = false) => new(status, tombstone, detail, outcomeCommitted);

}
