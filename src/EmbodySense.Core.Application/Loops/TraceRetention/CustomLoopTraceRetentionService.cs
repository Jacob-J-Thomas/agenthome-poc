using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Governance.Audit.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;

namespace EmbodySense.Core.Application.Loops.TraceRetention;

public sealed class CustomLoopTraceRetentionService
{
    private static readonly TimeSpan IntegrityWriteTimeout = TimeSpan.FromSeconds(30);
    private readonly ICustomLoopRunStore _store;
    private readonly IAuditLog _auditLog;
    private readonly TimeProvider _timeProvider;

    public CustomLoopTraceRetentionService(ICustomLoopRunStore store, IAuditLog auditLog, TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<CustomLoopTraceInspection?> InspectAsync(string runId, CancellationToken cancellationToken = default) => _store.InspectTraceAsync(runId, cancellationToken);

    public Task<CustomLoopTraceQuota> GetQuotaAsync(CancellationToken cancellationToken = default) => _store.GetTraceQuotaAsync(cancellationToken);

    public async Task<CustomLoopTraceDeletionResult> DeleteAsync(CustomLoopTraceDeletionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validationDetail = Validate(request);
        if (validationDetail is not null)
        {
            return Result(CustomLoopTraceDeletionStatus.Invalid, null, validationDetail);
        }

        var requestHash = CustomLoopTraceDeletionRequestHash.Compute(request);
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

            if (lookup.Status != CustomLoopTraceDeletionLookupStatus.PendingMutation)
            {
                return Result(CustomLoopTraceDeletionStatus.Invalid, null, "The deletion operation ledger contains an unsupported state.");
            }
        }
        else
        {
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

            if (!await TryAppendAuditAsync(CreateAudit(AuditSchema.Actions.LoopTraceDeletionIntent, AuditSchema.Outcomes.Requested, request, inspection, null), cancellationToken))
            {
                return Result(CustomLoopTraceDeletionStatus.AuditUnavailable, null, "The trace was not changed because its deletion-intent audit could not be recorded.");
            }
        }

        CustomLoopTraceDeletionStoreResult stored;
        using var mutationIntegrityWindow = new CancellationTokenSource(IntegrityWriteTimeout);
        try
        {
            var mutation = new CustomLoopTraceDeletionMutation(request, requestHash, _timeProvider.GetUtcNow().ToUniversalTime());
            stored = await _store.DeleteTerminalTraceAsync(mutation, mutationIntegrityWindow.Token);
        }
        catch (Exception exception)
        {
            try
            {
                var inspection = await _store.InspectTraceAsync(request.RunId, mutationIntegrityWindow.Token);
                if (inspection?.Tombstone is not null
                    && string.Equals(inspection.Tombstone.DeletionOperationId, request.OperationId, StringComparison.Ordinal)
                    && string.Equals(inspection.Tombstone.DeletionRequestHash, requestHash, StringComparison.Ordinal))
                {
                    return Result(CustomLoopTraceDeletionStatus.CommittedWithAuditWarning, inspection.Tombstone, "The trace deletion committed, but its durable operation outcome requires recovery before audit completion.");
                }
            }
            catch (Exception)
            {
            }

            return Result(CustomLoopTraceDeletionStatus.Invalid, null, $"The trace deletion could not be persisted safely: {exception.GetType().Name}.");
        }

        if (!stored.IsCommitted)
        {
            return MapRejectedStoreResult(stored);
        }

        return await CompleteOutcomeAsync(request, stored, isReplay: lookup.Operation is not null);
    }

    private async Task<CustomLoopTraceDeletionResult> CompleteOutcomeAsync(CustomLoopTraceDeletionRequest request, CustomLoopTraceDeletionStoreResult stored, bool isReplay, DateTimeOffset? outcomeAuditStartedAtUtc = null)
    {
        if (!stored.IsCommitted || stored.Tombstone is null)
        {
            return MapRejectedStoreResult(stored);
        }

        if (stored.Integrity == CustomLoopTraceDeletionIntegrity.Complete)
        {
            return Result(CustomLoopTraceDeletionStatus.Replayed, stored.Tombstone, "The confirmed trace deletion was already committed and fully audited.");
        }

        if (stored.Integrity == CustomLoopTraceDeletionIntegrity.CommittedWithAuditWarning)
        {
            return Result(CustomLoopTraceDeletionStatus.CommittedWithAuditWarning, stored.Tombstone, "The trace deletion is committed; its original outcome-audit warning remains visible.");
        }

        if (stored.Integrity == CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted)
        {
            return await ResolveInterruptedOutcomeAuditAsync(request, stored.Tombstone, outcomeAuditStartedAtUtc);
        }

        if (stored.Integrity != CustomLoopTraceDeletionIntegrity.PendingOutcomeAudit)
        {
            return Result(CustomLoopTraceDeletionStatus.CommittedWithAuditWarning, stored.Tombstone, "The trace deletion is committed, but its durable audit-integrity state requires review.");
        }

        using var integrityWindow = new CancellationTokenSource(IntegrityWriteTimeout);
        try
        {
            var started = await _store.MarkTraceDeletionOutcomeAsync(request.OperationId, CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted, integrityWindow.Token);
            if (started == CustomLoopTraceDeletionAuditMarkStatus.AlreadyMarked)
            {
                var existing = await _store.GetTraceDeletionOperationAsync(request.OperationId, integrityWindow.Token);
                if (existing.Operation?.Integrity == CustomLoopTraceDeletionIntegrity.Complete)
                {
                    return Result(CustomLoopTraceDeletionStatus.Replayed, existing.Operation.Tombstone ?? stored.Tombstone, "The confirmed trace deletion was completed by its existing outcome-audit owner.");
                }

                if (existing.Operation?.Integrity == CustomLoopTraceDeletionIntegrity.CommittedWithAuditWarning)
                {
                    return Result(CustomLoopTraceDeletionStatus.CommittedWithAuditWarning, existing.Operation.Tombstone ?? stored.Tombstone, "The trace deletion is committed; its original outcome-audit warning remains visible.");
                }

                return await ResolveInterruptedOutcomeAuditAsync(request, existing.Operation?.Tombstone ?? stored.Tombstone, existing.Operation?.UpdatedAtUtc);
            }

            if (started != CustomLoopTraceDeletionAuditMarkStatus.Marked)
            {
                return Result(CustomLoopTraceDeletionStatus.CommittedWithAuditWarning, stored.Tombstone, "The trace deletion is committed, but its outcome-audit attempt could not be durably started.");
            }
        }
        catch (Exception)
        {
            return Result(CustomLoopTraceDeletionStatus.CommittedWithAuditWarning, stored.Tombstone, "The trace deletion is committed, but its outcome-audit attempt could not be durably started.");
        }

        var audited = await TryAppendAuditAsync(CreateAudit(AuditSchema.Actions.LoopTraceDeletionOutcome, AuditSchema.Outcomes.Succeeded, request, null, stored.Tombstone), integrityWindow.Token);
        var desiredIntegrity = audited ? CustomLoopTraceDeletionIntegrity.Complete : CustomLoopTraceDeletionIntegrity.CommittedWithAuditWarning;
        try
        {
            var mark = await _store.MarkTraceDeletionOutcomeAsync(request.OperationId, desiredIntegrity, integrityWindow.Token);
            if (mark is not CustomLoopTraceDeletionAuditMarkStatus.Marked and not CustomLoopTraceDeletionAuditMarkStatus.AlreadyMarked)
            {
                return Result(CustomLoopTraceDeletionStatus.CommittedWithAuditWarning, stored.Tombstone, "The trace deletion is committed, but its durable outcome-integrity marker requires review.");
            }

            var refreshed = await _store.GetTraceDeletionOperationAsync(request.OperationId, integrityWindow.Token);
            var tombstone = refreshed.Operation?.Tombstone ?? stored.Tombstone;
            var integrity = refreshed.Operation?.Integrity ?? desiredIntegrity;
            if (integrity == CustomLoopTraceDeletionIntegrity.Complete)
            {
                var status = isReplay ? CustomLoopTraceDeletionStatus.Replayed : CustomLoopTraceDeletionStatus.Deleted;
                return Result(status, tombstone, isReplay ? "The confirmed trace deletion was recovered and completed." : "The terminal trace content was replaced by an audited tombstone.");
            }

            return Result(CustomLoopTraceDeletionStatus.CommittedWithAuditWarning, tombstone, "The terminal trace content was deleted, but its outcome audit could not be recorded.");
        }
        catch (Exception)
        {
            return Result(CustomLoopTraceDeletionStatus.CommittedWithAuditWarning, stored.Tombstone, "The trace deletion is committed, but its durable outcome-integrity marker could not be completed.");
        }
    }

    private async Task<CustomLoopTraceDeletionResult> ResolveInterruptedOutcomeAuditAsync(CustomLoopTraceDeletionRequest request, CustomLoopTraceTombstone tombstone, DateTimeOffset? outcomeAuditStartedAtUtc)
    {
        if (outcomeAuditStartedAtUtc is not null && outcomeAuditStartedAtUtc.Value > _timeProvider.GetUtcNow().ToUniversalTime() - IntegrityWriteTimeout)
        {
            return Result(CustomLoopTraceDeletionStatus.CommittedWithAuditWarning, tombstone, "The trace deletion is committed and its existing outcome-audit owner is still active; retry after that bounded integrity window completes.");
        }

        using var integrityWindow = new CancellationTokenSource(IntegrityWriteTimeout);
        try
        {
            await _store.MarkTraceDeletionOutcomeAsync(request.OperationId, CustomLoopTraceDeletionIntegrity.CommittedWithAuditWarning, integrityWindow.Token);
            var refreshed = await _store.GetTraceDeletionOperationAsync(request.OperationId, integrityWindow.Token);
            if (refreshed.Operation?.Integrity == CustomLoopTraceDeletionIntegrity.Complete)
            {
                return Result(CustomLoopTraceDeletionStatus.Replayed, refreshed.Operation.Tombstone ?? tombstone, "The confirmed trace deletion was completed by its original outcome-audit owner.");
            }

            return Result(CustomLoopTraceDeletionStatus.CommittedWithAuditWarning, refreshed.Operation?.Tombstone ?? tombstone, "The trace deletion is committed; a prior outcome-audit attempt was interrupted, so audit integrity requires review and the audit was not duplicated.");
        }
        catch (Exception)
        {
            return Result(CustomLoopTraceDeletionStatus.CommittedWithAuditWarning, tombstone, "The trace deletion is committed; an interrupted outcome-audit attempt and its incomplete warning marker require review.");
        }
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

    private static CustomLoopTraceDeletionResult Result(CustomLoopTraceDeletionStatus status, CustomLoopTraceTombstone? tombstone, string detail) => new(status, tombstone, detail);

}
