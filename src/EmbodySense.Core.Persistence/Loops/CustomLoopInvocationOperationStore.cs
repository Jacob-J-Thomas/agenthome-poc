using EmbodySense.Core.Application.Loops.Models;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.ReceiptRetention;
using EmbodySense.Core.Application.Loops.ReceiptRetention.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Persistence.Loops;

public sealed class CustomLoopInvocationOperationStore : ICustomLoopInvocationOperationStore
{
    private const string MutationLockFileName = ".custom-loop-mutations.lock";
    private const string RetentionOperationFileName = "active.json";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProcessGates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        MaxDepth = CustomLoopJsonDepthPolicy.ShallowReceiptMaximumDepth,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    private readonly string _root;
    private readonly string _retentionRoot;
    private readonly CustomLoopArtifactPathGuard _pathGuard;
    private readonly SemaphoreSlim _processGate;
    private readonly TimeProvider _timeProvider;

    public CustomLoopInvocationOperationStore(WorkspacePaths paths, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _root = Path.GetFullPath(paths.CustomLoopInvocationOperationsPath);
        _retentionRoot = Path.GetFullPath(paths.CustomLoopInvocationReceiptRetentionPath);
        _pathGuard = new CustomLoopArtifactPathGuard(paths.RootPath);
        _processGate = ProcessGates.GetOrAdd(_root, _ => new SemaphoreSlim(1, 1));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<CustomLoopInvocationOperationStoreResult> BeginAsync(CustomLoopInvocationOperation operation, CancellationToken cancellationToken = default)
    {
        Validate(operation, requirePending: true);
        if (operation.BindingState != CustomLoopInvocationBindingState.Unbound)
        {
            throw new ArgumentException("New invocation operation must begin without a conversation or context binding.", nameof(operation));
        }

        await _processGate.WaitAsync(cancellationToken);
        try
        {
            using var workspaceLock = _pathGuard.AcquireExclusiveMutationLock(_root);
            var existing = await ReadIfExistsAsync(operation.OperationId, cancellationToken);
            if (existing is not null)
            {
                return SameEnvelope(existing, operation)
                    ? new CustomLoopInvocationOperationStoreResult(CustomLoopInvocationOperationStoreStatus.Replayed, existing)
                    : new CustomLoopInvocationOperationStoreResult(CustomLoopInvocationOperationStoreStatus.Conflict, existing);
            }

            var retention = await ReadRetentionOperationAsync(cancellationToken);
            if (retention?.State is CustomLoopInvocationReceiptRetentionOperationState.Reserved or CustomLoopInvocationReceiptRetentionOperationState.IntentAuditRecorded)
            {
                return new CustomLoopInvocationOperationStoreResult(CustomLoopInvocationOperationStoreStatus.RetentionRequired, null);
            }

            var json = SerializeBounded(operation);
            if (!HasCapacityForNewOperation(Encoding.UTF8.GetByteCount(json)))
            {
                return new CustomLoopInvocationOperationStoreResult(CustomLoopInvocationOperationStoreStatus.LimitExceeded, null);
            }

            await WriteAsync(operation, json, cancellationToken);
            return new CustomLoopInvocationOperationStoreResult(CustomLoopInvocationOperationStoreStatus.Created, operation);
        }
        finally
        {
            _processGate.Release();
        }
    }

    public async Task<CustomLoopInvocationOperationStoreResult> BindAsync(CustomLoopInvocationOperation operation, CancellationToken cancellationToken = default)
    {
        Validate(operation, requirePending: operation.State == CustomLoopInvocationOperationState.Pending);
        if (operation.BindingState is not (CustomLoopInvocationBindingState.ConversationNotFound
            or CustomLoopInvocationBindingState.ConversationWorkspaceExecutionBusy
            or CustomLoopInvocationBindingState.ConversationInvalid
            or CustomLoopInvocationBindingState.CapturedContext
            or CustomLoopInvocationBindingState.CapturedContextNotFound))
        {
            throw new ArgumentException("Invocation binding must identify its conversation and optional captured context.", nameof(operation));
        }

        await _processGate.WaitAsync(cancellationToken);
        try
        {
            using var workspaceLock = _pathGuard.AcquireExclusiveMutationLock(_root);
            var existing = await ReadIfExistsAsync(operation.OperationId, cancellationToken);
            if (existing is null)
            {
                return new CustomLoopInvocationOperationStoreResult(CustomLoopInvocationOperationStoreStatus.NotFound, null);
            }

            if (!SameEnvelope(existing, operation))
            {
                return new CustomLoopInvocationOperationStoreResult(CustomLoopInvocationOperationStoreStatus.Conflict, existing);
            }

            if (existing.BindingState != CustomLoopInvocationBindingState.Unbound)
            {
                if (SameBinding(existing, operation))
                {
                    return new CustomLoopInvocationOperationStoreResult(CustomLoopInvocationOperationStoreStatus.Replayed, existing);
                }

                var canTerminalizeCapturedNotFound = existing.State == CustomLoopInvocationOperationState.Pending
                    && existing.BindingState == CustomLoopInvocationBindingState.CapturedContext
                    && operation.BindingState == CustomLoopInvocationBindingState.CapturedContextNotFound
                    && string.Equals(existing.InvokingConversationId, operation.InvokingConversationId, StringComparison.Ordinal)
                    && string.Equals(existing.ContextIdentityHash, operation.ContextIdentityHash, StringComparison.Ordinal);
                if (!canTerminalizeCapturedNotFound)
                {
                    return new CustomLoopInvocationOperationStoreResult(CustomLoopInvocationOperationStoreStatus.Conflict, existing);
                }
            }

            if (existing.State != CustomLoopInvocationOperationState.Pending || operation.State != CustomLoopInvocationOperationState.Pending)
            {
                return new CustomLoopInvocationOperationStoreResult(CustomLoopInvocationOperationStoreStatus.Conflict, existing);
            }

            if (operation.UpdatedAtUtc < existing.UpdatedAtUtc)
            {
                return new CustomLoopInvocationOperationStoreResult(CustomLoopInvocationOperationStoreStatus.Conflict, existing);
            }

            var normalized = operation with { CreatedAtUtc = existing.CreatedAtUtc };
            Validate(normalized, requirePending: normalized.State == CustomLoopInvocationOperationState.Pending);
            var json = SerializeBounded(normalized);
            if (!HasCapacity(Encoding.UTF8.GetByteCount(json), normalized.OperationId))
            {
                return new CustomLoopInvocationOperationStoreResult(CustomLoopInvocationOperationStoreStatus.LimitExceeded, existing);
            }

            await WriteAsync(normalized, json, cancellationToken);
            return new CustomLoopInvocationOperationStoreResult(CustomLoopInvocationOperationStoreStatus.Bound, normalized);
        }
        finally
        {
            _processGate.Release();
        }
    }

    public async Task<CustomLoopInvocationOperation?> GetAsync(string operationId, CancellationToken cancellationToken = default)
    {
        var safeOperationId = CustomLoopArtifactIdentifier.Require(operationId, nameof(operationId), CustomLoopLimits.MaxMutationOperationIdCharacters);
        await _processGate.WaitAsync(cancellationToken);
        try
        {
            return await ReadIfExistsAsync(safeOperationId, cancellationToken);
        }
        finally
        {
            _processGate.Release();
        }
    }

    public async Task<CustomLoopInvocationOperationStoreResult> CompleteAsync(CustomLoopInvocationOperation operation, CancellationToken cancellationToken = default)
    {
        Validate(operation, requirePending: false);
        if (operation.State != CustomLoopInvocationOperationState.Complete)
        {
            throw new ArgumentException("Completed invocation operation must have Complete state.", nameof(operation));
        }

        await _processGate.WaitAsync(cancellationToken);
        try
        {
            using var workspaceLock = _pathGuard.AcquireExclusiveMutationLock(_root);
            var existing = await ReadIfExistsAsync(operation.OperationId, cancellationToken);
            if (existing is null)
            {
                return new CustomLoopInvocationOperationStoreResult(CustomLoopInvocationOperationStoreStatus.NotFound, null);
            }

            if (!SameEnvelope(existing, operation) || !SameBinding(existing, operation))
            {
                return new CustomLoopInvocationOperationStoreResult(CustomLoopInvocationOperationStoreStatus.Conflict, existing);
            }

            var normalized = operation with { CreatedAtUtc = existing.CreatedAtUtc };
            if (existing.State == CustomLoopInvocationOperationState.Complete)
            {
                return SameCompletedOperation(existing, normalized)
                    ? new CustomLoopInvocationOperationStoreResult(CustomLoopInvocationOperationStoreStatus.Replayed, existing)
                    : new CustomLoopInvocationOperationStoreResult(CustomLoopInvocationOperationStoreStatus.Conflict, existing);
            }

            if (operation.UpdatedAtUtc < existing.UpdatedAtUtc)
            {
                return new CustomLoopInvocationOperationStoreResult(CustomLoopInvocationOperationStoreStatus.Conflict, existing);
            }

            Validate(normalized, requirePending: false);
            var json = SerializeBounded(normalized);
            if (!HasCapacity(Encoding.UTF8.GetByteCount(json), normalized.OperationId))
            {
                return new CustomLoopInvocationOperationStoreResult(CustomLoopInvocationOperationStoreStatus.LimitExceeded, existing);
            }

            await WriteAsync(normalized, json, cancellationToken);
            return new CustomLoopInvocationOperationStoreResult(CustomLoopInvocationOperationStoreStatus.Completed, normalized);
        }
        finally
        {
            _processGate.Release();
        }
    }

    public async Task<CustomLoopInvocationReceiptRetentionReservationResult> ReserveCompletedReceiptRetentionAsync(CustomLoopInvocationReceiptRetentionRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRetentionRequest(request);
        await _processGate.WaitAsync(cancellationToken);
        try
        {
            using var workspaceLock = _pathGuard.AcquireExclusiveMutationLock(_root);
            var existing = await ReadRetentionOperationAsync(cancellationToken);
            if (existing is not null
                && existing.State is not CustomLoopInvocationReceiptRetentionOperationState.OutcomeAuditRecorded
                    and not CustomLoopInvocationReceiptRetentionOperationState.AbandonedConflictAuditRecorded)
            {
                var now = _timeProvider.GetUtcNow().ToUniversalTime();
                if (existing.State is CustomLoopInvocationReceiptRetentionOperationState.Reserved or CustomLoopInvocationReceiptRetentionOperationState.IntentAuditRecorded)
                {
                    if (IsInsideOwnershipWindow(existing, now))
                    {
                        return new CustomLoopInvocationReceiptRetentionReservationResult(CustomLoopInvocationReceiptRetentionReservationStatus.OperationInProgress, existing);
                    }

                    var resumed = existing with { OwnershipStartedAtUtc = Max(existing.OwnershipStartedAtUtc, now), UpdatedAtUtc = Max(existing.UpdatedAtUtc, now) };
                    await WriteRetentionOperationAsync(resumed, cancellationToken);
                    var resumedStatus = resumed.State == CustomLoopInvocationReceiptRetentionOperationState.Reserved
                        ? CustomLoopInvocationReceiptRetentionReservationStatus.Reserved
                        : CustomLoopInvocationReceiptRetentionReservationStatus.ReadyToCommit;
                    return new CustomLoopInvocationReceiptRetentionReservationResult(resumedStatus, resumed);
                }

                if (existing.State == CustomLoopInvocationReceiptRetentionOperationState.OutcomeAuditStarted)
                {
                    if (IsInsideOwnershipWindow(existing, now))
                    {
                        return new CustomLoopInvocationReceiptRetentionReservationResult(CustomLoopInvocationReceiptRetentionReservationStatus.OperationInProgress, existing);
                    }

                    var warned = existing with { UpdatedAtUtc = Max(existing.UpdatedAtUtc, now), State = CustomLoopInvocationReceiptRetentionOperationState.CommittedWithAuditWarning };
                    await WriteRetentionOperationAsync(warned, cancellationToken);
                    return new CustomLoopInvocationReceiptRetentionReservationResult(CustomLoopInvocationReceiptRetentionReservationStatus.OutcomeCommitted, warned);
                }

                if (existing.State == CustomLoopInvocationReceiptRetentionOperationState.OutcomeCommitted)
                {
                    if (IsInsideOwnershipWindow(existing, now))
                    {
                        return new CustomLoopInvocationReceiptRetentionReservationResult(CustomLoopInvocationReceiptRetentionReservationStatus.OperationInProgress, existing);
                    }

                    var resumed = existing with { OwnershipStartedAtUtc = Max(existing.OwnershipStartedAtUtc, now), UpdatedAtUtc = Max(existing.UpdatedAtUtc, now) };
                    await WriteRetentionOperationAsync(resumed, cancellationToken);
                    return new CustomLoopInvocationReceiptRetentionReservationResult(CustomLoopInvocationReceiptRetentionReservationStatus.OutcomeCommitted, resumed);
                }

                if (existing.State == CustomLoopInvocationReceiptRetentionOperationState.CommittedWithAuditWarning)
                {
                    return new CustomLoopInvocationReceiptRetentionReservationResult(CustomLoopInvocationReceiptRetentionReservationStatus.OutcomeCommitted, existing);
                }

                if (existing.State == CustomLoopInvocationReceiptRetentionOperationState.AbandonedCandidateChanged)
                {
                    if (IsInsideOwnershipWindow(existing, now))
                    {
                        return new CustomLoopInvocationReceiptRetentionReservationResult(CustomLoopInvocationReceiptRetentionReservationStatus.OperationInProgress, existing);
                    }

                    var resumed = existing with { OwnershipStartedAtUtc = Max(existing.OwnershipStartedAtUtc, now), UpdatedAtUtc = Max(existing.UpdatedAtUtc, now) };
                    await WriteRetentionOperationAsync(resumed, cancellationToken);
                    return new CustomLoopInvocationReceiptRetentionReservationResult(CustomLoopInvocationReceiptRetentionReservationStatus.ConflictPendingAudit, resumed);
                }

                if (existing.State == CustomLoopInvocationReceiptRetentionOperationState.AbandonedConflictAuditStarted)
                {
                    if (IsInsideOwnershipWindow(existing, now))
                    {
                        return new CustomLoopInvocationReceiptRetentionReservationResult(CustomLoopInvocationReceiptRetentionReservationStatus.OperationInProgress, existing);
                    }

                    var warned = existing with { UpdatedAtUtc = Max(existing.UpdatedAtUtc, now), State = CustomLoopInvocationReceiptRetentionOperationState.AbandonedWithAuditWarning };
                    await WriteRetentionOperationAsync(warned, cancellationToken);
                    return new CustomLoopInvocationReceiptRetentionReservationResult(CustomLoopInvocationReceiptRetentionReservationStatus.ConflictPendingAudit, warned);
                }

                if (existing.State == CustomLoopInvocationReceiptRetentionOperationState.AbandonedWithAuditWarning)
                {
                    return new CustomLoopInvocationReceiptRetentionReservationResult(CustomLoopInvocationReceiptRetentionReservationStatus.ConflictPendingAudit, existing);
                }

                throw new FormatException("The invocation-receipt retention journal contains an unsupported state.");
            }

            var candidates = new List<CustomLoopInvocationReceiptRetentionCandidate>();
            foreach (var path in EnumerateOperationPaths())
            {
                var operationId = Path.GetFileNameWithoutExtension(path);
                var operation = await ReadIfExistsAsync(operationId, cancellationToken) ?? throw new FormatException($"Custom-loop invocation operation `{path}` disappeared during its retention scan.");
                if (operation.State != CustomLoopInvocationOperationState.Complete || operation.UpdatedAtUtc > request.ReplayCutoffUtc)
                {
                    continue;
                }

                var bytes = await _pathGuard.ReadAllBytesAsync(_root, path, CustomLoopLimits.MaxInvocationOperationUtf8Bytes, "Custom-loop invocation operation", cancellationToken);
                candidates.Add(new CustomLoopInvocationReceiptRetentionCandidate(operation.OperationId, operation.UpdatedAtUtc, Hash(bytes), bytes.LongLength));
            }

            var ordered = candidates.OrderBy(candidate => candidate.CompletedAtUtc).ThenBy(candidate => candidate.OperationId, StringComparer.Ordinal).ToArray();
            if (ordered.Length == 0)
            {
                return new CustomLoopInvocationReceiptRetentionReservationResult(CustomLoopInvocationReceiptRetentionReservationStatus.NothingEligible, null);
            }

            var persistedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime();
            var reservedAtUtc = persistedAtUtc < request.RequestedAtUtc ? request.RequestedAtUtc : persistedAtUtc;
            var retention = new CustomLoopInvocationReceiptRetentionOperation(
                CustomLoopInvocationReceiptRetentionOperation.CurrentSchemaVersion,
                request.OperationId,
                request.Actor,
                request.Surface,
                request.RequestedAtUtc,
                request.ReplayCutoffUtc,
                reservedAtUtc,
                reservedAtUtc,
                ordered,
                CustomLoopInvocationReceiptRetentionOperationState.Reserved,
                0,
                0);
            await WriteRetentionOperationAsync(retention, cancellationToken);
            return new CustomLoopInvocationReceiptRetentionReservationResult(CustomLoopInvocationReceiptRetentionReservationStatus.Reserved, retention);
        }
        finally
        {
            _processGate.Release();
        }
    }

    public async Task<CustomLoopInvocationReceiptRetentionOperation> MarkReceiptRetentionIntentAuditedAsync(string operationId, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default)
    {
        return await AdvanceRetentionOperationAsync(operationId, updatedAtUtc, CustomLoopInvocationReceiptRetentionOperationState.Reserved, CustomLoopInvocationReceiptRetentionOperationState.IntentAuditRecorded, cancellationToken);
    }

    public async Task<CustomLoopInvocationReceiptRetentionOperation> CommitCompletedReceiptRetentionAsync(string operationId, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default)
    {
        var safeOperationId = CustomLoopArtifactIdentifier.Require(operationId, nameof(operationId), CustomLoopLimits.MaxMutationOperationIdCharacters);
        RequireUtc(updatedAtUtc, nameof(updatedAtUtc));
        await _processGate.WaitAsync(cancellationToken);
        try
        {
            using var workspaceLock = _pathGuard.AcquireExclusiveMutationLock(_root);
            var operation = await RequireRetentionOperationAsync(safeOperationId, cancellationToken);
            if (operation.State is CustomLoopInvocationReceiptRetentionOperationState.OutcomeCommitted or CustomLoopInvocationReceiptRetentionOperationState.OutcomeAuditRecorded)
            {
                return operation;
            }

            if (operation.State != CustomLoopInvocationReceiptRetentionOperationState.IntentAuditRecorded)
            {
                throw new InvalidOperationException("Invocation-receipt retention cannot delete artifacts before its intent audit is durable.");
            }

            var retainedPaths = new List<string>();
            var candidateChanged = false;
            foreach (var candidate in operation.Candidates)
            {
                var path = _pathGuard.GetFilePath(_root, candidate.OperationId + ".json");
                if (!File.Exists(path))
                {
                    candidateChanged = true;
                    continue;
                }

                var receipt = await ReadIfExistsAsync(candidate.OperationId, cancellationToken) ?? throw new FormatException($"Invocation receipt `{candidate.OperationId}` disappeared during retention validation.");
                var bytes = await _pathGuard.ReadAllBytesAsync(_root, path, CustomLoopLimits.MaxInvocationOperationUtf8Bytes, "Custom-loop invocation operation", cancellationToken);
                if (receipt.State != CustomLoopInvocationOperationState.Complete
                    || receipt.UpdatedAtUtc != candidate.CompletedAtUtc
                    || receipt.UpdatedAtUtc > operation.ReplayCutoffUtc
                    || bytes.LongLength != candidate.ArtifactUtf8Bytes
                    || !string.Equals(Hash(bytes), candidate.ArtifactHash, StringComparison.Ordinal))
                {
                    candidateChanged = true;
                    continue;
                }

                retainedPaths.Add(path);
            }

            if (candidateChanged)
            {
                var abandoned = operation with
                {
                    UpdatedAtUtc = Max(updatedAtUtc, operation.UpdatedAtUtc),
                    State = CustomLoopInvocationReceiptRetentionOperationState.AbandonedCandidateChanged,
                    DeletedReceiptCount = 0,
                    DeletedReceiptUtf8Bytes = 0
                };
                await WriteRetentionOperationAsync(abandoned, cancellationToken);
                return abandoned;
            }

            foreach (var path in retainedPaths)
            {
                _pathGuard.DeleteFile(_root, path);
            }

            var completed = operation with
            {
                UpdatedAtUtc = Max(updatedAtUtc, operation.UpdatedAtUtc),
                State = CustomLoopInvocationReceiptRetentionOperationState.OutcomeCommitted,
                DeletedReceiptCount = operation.Candidates.Length,
                DeletedReceiptUtf8Bytes = operation.Candidates.Sum(candidate => candidate.ArtifactUtf8Bytes)
            };
            await WriteRetentionOperationAsync(completed, cancellationToken);
            return completed;
        }
        finally
        {
            _processGate.Release();
        }
    }

    public async Task<CustomLoopInvocationReceiptRetentionOperation> MarkReceiptRetentionOutcomeAuditedAsync(string operationId, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default)
    {
        return await AdvanceRetentionOperationAsync(operationId, updatedAtUtc, CustomLoopInvocationReceiptRetentionOperationState.OutcomeAuditStarted, CustomLoopInvocationReceiptRetentionOperationState.OutcomeAuditRecorded, cancellationToken);
    }

    public async Task<CustomLoopInvocationReceiptRetentionOperation> MarkReceiptRetentionOutcomeAuditStartedAsync(string operationId, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default)
    {
        return await AdvanceRetentionOperationAsync(operationId, updatedAtUtc, CustomLoopInvocationReceiptRetentionOperationState.OutcomeCommitted, CustomLoopInvocationReceiptRetentionOperationState.OutcomeAuditStarted, cancellationToken);
    }

    public async Task<CustomLoopInvocationReceiptRetentionOperation> MarkReceiptRetentionOutcomeAuditWarningAsync(string operationId, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default)
    {
        return await AdvanceRetentionOperationAsync(operationId, updatedAtUtc, CustomLoopInvocationReceiptRetentionOperationState.OutcomeAuditStarted, CustomLoopInvocationReceiptRetentionOperationState.CommittedWithAuditWarning, cancellationToken);
    }

    public async Task<CustomLoopInvocationReceiptRetentionOperation> MarkReceiptRetentionConflictAuditStartedAsync(string operationId, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default)
    {
        return await AdvanceRetentionOperationAsync(operationId, updatedAtUtc, CustomLoopInvocationReceiptRetentionOperationState.AbandonedCandidateChanged, CustomLoopInvocationReceiptRetentionOperationState.AbandonedConflictAuditStarted, cancellationToken);
    }

    public async Task<CustomLoopInvocationReceiptRetentionOperation> MarkReceiptRetentionConflictAuditedAsync(string operationId, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default)
    {
        return await AdvanceRetentionOperationAsync(operationId, updatedAtUtc, CustomLoopInvocationReceiptRetentionOperationState.AbandonedConflictAuditStarted, CustomLoopInvocationReceiptRetentionOperationState.AbandonedConflictAuditRecorded, cancellationToken);
    }

    public async Task<CustomLoopInvocationReceiptRetentionOperation> MarkReceiptRetentionConflictAuditWarningAsync(string operationId, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default)
    {
        return await AdvanceRetentionOperationAsync(operationId, updatedAtUtc, CustomLoopInvocationReceiptRetentionOperationState.AbandonedConflictAuditStarted, CustomLoopInvocationReceiptRetentionOperationState.AbandonedWithAuditWarning, cancellationToken);
    }

    private async Task<CustomLoopInvocationOperation?> ReadIfExistsAsync(string operationId, CancellationToken cancellationToken)
    {
        if (!_pathGuard.DirectoryExists(_root))
        {
            return null;
        }

        var path = _pathGuard.GetFilePath(_root, operationId + ".json");
        if (!File.Exists(path))
        {
            return null;
        }

        var bytes = await _pathGuard.ReadAllBytesAsync(_root, path, CustomLoopLimits.MaxInvocationOperationUtf8Bytes, "Custom-loop invocation operation", cancellationToken);
        CustomLoopJsonDepthPolicy.ValidatePersistedJsonDepth(bytes, JsonOptions.MaxDepth, "Custom-loop invocation operation", path);
        CustomLoopInvocationOperation? operation;
        try
        {
            operation = JsonSerializer.Deserialize<CustomLoopInvocationOperation>(bytes, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new FormatException($"Custom-loop invocation operation `{path}` is invalid JSON.", exception);
        }

        Validate(operation, requirePending: operation?.State == CustomLoopInvocationOperationState.Pending);
        if (!string.Equals(operation!.OperationId, operationId, StringComparison.Ordinal))
        {
            throw new FormatException($"Custom-loop invocation operation filename `{operationId}` does not match embedded id `{operation.OperationId}`.");
        }

        return operation;
    }

    private async Task WriteAsync(CustomLoopInvocationOperation operation, string json, CancellationToken cancellationToken)
    {
        var path = _pathGuard.GetFilePath(_root, operation.OperationId + ".json");
        await _pathGuard.WriteTextAtomicallyAsync(_root, path, json, cancellationToken);
    }

    private static string SerializeBounded(CustomLoopInvocationOperation operation)
    {
        string json;
        try
        {
            json = JsonSerializer.Serialize(operation, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw CustomLoopJsonDepthPolicy.SerializationDepthException("Custom-loop invocation operation", JsonOptions.MaxDepth, exception);
        }

        if (Encoding.UTF8.GetByteCount(json) > CustomLoopLimits.MaxInvocationOperationUtf8Bytes)
        {
            throw new ArgumentException($"Custom-loop invocation operation exceeds {CustomLoopLimits.MaxInvocationOperationUtf8Bytes} UTF-8 bytes.", nameof(operation));
        }

        return json;
    }

    private bool HasCapacityForNewOperation(long newArtifactBytes) => HasCapacity(newArtifactBytes, replacingOperationId: null);

    private bool HasCapacity(long newArtifactBytes, string? replacingOperationId)
    {
        var paths = EnumerateOperationPaths();
        if (replacingOperationId is null && paths.Count >= CustomLoopLimits.MaxInvocationOperationReceiptsPerWorkspace)
        {
            return false;
        }

        long accountedBytes = 0;
        foreach (var path in paths)
        {
            if (replacingOperationId is not null && string.Equals(Path.GetFileNameWithoutExtension(path), replacingOperationId, StringComparison.Ordinal))
            {
                continue;
            }

            accountedBytes = checked(accountedBytes + new FileInfo(path).Length);
        }

        if (accountedBytes > CustomLoopLimits.MaxInvocationOperationWorkspaceUtf8Bytes - newArtifactBytes)
        {
            return false;
        }

        return true;
    }

    private IReadOnlyList<string> EnumerateOperationPaths()
    {
        if (!_pathGuard.DirectoryExists(_root))
        {
            return [];
        }

        if (Directory.EnumerateDirectories(_root, "*", SearchOption.TopDirectoryOnly).Any())
        {
            throw new FormatException("Custom-loop invocation receipt storage cannot contain subdirectories.");
        }

        var paths = new List<string>();
        foreach (var path in Directory.EnumerateFiles(_root, "*", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.Ordinal))
        {
            var fileName = Path.GetFileName(path);
            if (string.Equals(fileName, MutationLockFileName, StringComparison.Ordinal))
            {
                continue;
            }

            if (IsAtomicWriteTemp(fileName, target => IsInvocationOperationFileName(target)))
            {
                _pathGuard.DeleteFile(_root, path);
                continue;
            }

            var operationId = Path.GetFileNameWithoutExtension(fileName);
            if (!IsInvocationOperationFileName(fileName))
            {
                throw new FormatException($"Custom-loop invocation receipt artifact `{path}` has an unsafe filename.");
            }

            paths.Add(_pathGuard.GetFilePath(_root, fileName));
        }

        if (paths.Count > CustomLoopLimits.MaxInvocationOperationReceiptsPerWorkspace)
        {
            throw new FormatException($"Custom-loop invocation receipt storage exceeds its explicit {CustomLoopLimits.MaxInvocationOperationReceiptsPerWorkspace}-artifact limit.");
        }

        return paths;
    }

    private async Task<CustomLoopInvocationReceiptRetentionOperation> AdvanceRetentionOperationAsync(
        string operationId,
        DateTimeOffset updatedAtUtc,
        CustomLoopInvocationReceiptRetentionOperationState expectedState,
        CustomLoopInvocationReceiptRetentionOperationState nextState,
        CancellationToken cancellationToken)
    {
        var safeOperationId = CustomLoopArtifactIdentifier.Require(operationId, nameof(operationId), CustomLoopLimits.MaxMutationOperationIdCharacters);
        RequireUtc(updatedAtUtc, nameof(updatedAtUtc));
        await _processGate.WaitAsync(cancellationToken);
        try
        {
            using var workspaceLock = _pathGuard.AcquireExclusiveMutationLock(_root);
            var operation = await RequireRetentionOperationAsync(safeOperationId, cancellationToken);
            if (operation.State == nextState)
            {
                return operation;
            }

            if (operation.State != expectedState)
            {
                throw new InvalidOperationException($"Invocation-receipt retention expected `{expectedState}` but found `{operation.State}`.");
            }

            var advanced = operation with { UpdatedAtUtc = Max(updatedAtUtc, operation.UpdatedAtUtc), State = nextState };
            await WriteRetentionOperationAsync(advanced, cancellationToken);
            return advanced;
        }
        finally
        {
            _processGate.Release();
        }
    }

    private async Task<CustomLoopInvocationReceiptRetentionOperation> RequireRetentionOperationAsync(string operationId, CancellationToken cancellationToken)
    {
        var operation = await ReadRetentionOperationAsync(cancellationToken);
        if (operation is null)
        {
            throw new InvalidOperationException("The invocation-receipt retention journal does not exist.");
        }

        if (!string.Equals(operation.OperationId, operationId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The invocation-receipt retention operation id does not own the active journal.");
        }

        return operation;
    }

    private async Task<CustomLoopInvocationReceiptRetentionOperation?> ReadRetentionOperationAsync(CancellationToken cancellationToken)
    {
        if (!_pathGuard.DirectoryExists(_retentionRoot))
        {
            return null;
        }

        var expectedPath = _pathGuard.GetFilePath(_retentionRoot, RetentionOperationFileName);
        if (Directory.EnumerateDirectories(_retentionRoot, "*", SearchOption.TopDirectoryOnly).Any())
        {
            throw new FormatException("Invocation-receipt retention storage cannot contain subdirectories.");
        }

        var files = Directory.EnumerateFiles(_retentionRoot, "*", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        foreach (var staleTemp in files.Where(path => IsAtomicWriteTemp(Path.GetFileName(path), target => string.Equals(target, RetentionOperationFileName, StringComparison.Ordinal))))
        {
            _pathGuard.DeleteFile(_retentionRoot, staleTemp);
        }

        files = Directory.EnumerateFiles(_retentionRoot, "*", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        if (files.Length == 0)
        {
            return null;
        }

        if (files.Length != 1 || !string.Equals(Path.GetFullPath(files[0]), expectedPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new FormatException("Invocation-receipt retention storage contains an unrecognized artifact.");
        }

        var bytes = await _pathGuard.ReadAllBytesAsync(_retentionRoot, expectedPath, CustomLoopLimits.MaxInvocationReceiptRetentionOperationUtf8Bytes, "Invocation-receipt retention operation", cancellationToken);
        CustomLoopJsonDepthPolicy.ValidatePersistedJsonDepth(bytes, JsonOptions.MaxDepth, "Invocation-receipt retention operation", expectedPath);
        CustomLoopInvocationReceiptRetentionOperation? operation;
        try
        {
            operation = JsonSerializer.Deserialize<CustomLoopInvocationReceiptRetentionOperation>(bytes, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new FormatException($"Invocation-receipt retention operation `{expectedPath}` is invalid JSON.", exception);
        }

        ValidateRetentionOperation(operation);
        return operation;
    }

    private async Task WriteRetentionOperationAsync(CustomLoopInvocationReceiptRetentionOperation operation, CancellationToken cancellationToken)
    {
        ValidateRetentionOperation(operation);
        string json;
        try
        {
            json = JsonSerializer.Serialize(operation, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw CustomLoopJsonDepthPolicy.SerializationDepthException("Invocation-receipt retention operation", JsonOptions.MaxDepth, exception);
        }

        if (Encoding.UTF8.GetByteCount(json) > CustomLoopLimits.MaxInvocationReceiptRetentionOperationUtf8Bytes)
        {
            throw new FormatException($"Invocation-receipt retention operation exceeds {CustomLoopLimits.MaxInvocationReceiptRetentionOperationUtf8Bytes} UTF-8 bytes.");
        }

        var path = _pathGuard.GetFilePath(_retentionRoot, RetentionOperationFileName);
        await _pathGuard.WriteTextAtomicallyAsync(_retentionRoot, path, json, cancellationToken);
    }

    private static void ValidateRetentionRequest(CustomLoopInvocationReceiptRetentionRequest? request)
    {
        ArgumentNullException.ThrowIfNull(request);
        CustomLoopArtifactIdentifier.Require(request.OperationId, nameof(request.OperationId), CustomLoopLimits.MaxMutationOperationIdCharacters);
        if (!IsBoundedText(request.Actor, CustomLoopLimits.MaxTraceReferenceCharacters) || !CustomLoopArtifactIdentifier.IsValid(request.Surface))
        {
            throw new ArgumentException("Invocation-receipt retention actor or surface is invalid.", nameof(request));
        }

        RequireUtc(request.RequestedAtUtc, nameof(request.RequestedAtUtc));
        RequireUtc(request.ReplayCutoffUtc, nameof(request.ReplayCutoffUtc));
        if (request.ReplayCutoffUtc != request.RequestedAtUtc - CustomLoopInvocationReceiptRetentionPolicy.MinimumReplayDuration)
        {
            throw new ArgumentException("Invocation-receipt retention must preserve the complete minimum replay duration.", nameof(request));
        }
    }

    private static void ValidateRetentionOperation(CustomLoopInvocationReceiptRetentionOperation? operation)
    {
        if (operation is null)
        {
            throw new FormatException("Invocation-receipt retention operation cannot be null.");
        }

        if (operation.SchemaVersion != CustomLoopInvocationReceiptRetentionOperation.CurrentSchemaVersion
            || !CustomLoopArtifactIdentifier.IsValid(operation.OperationId, CustomLoopLimits.MaxMutationOperationIdCharacters)
            || !IsBoundedText(operation.Actor, CustomLoopLimits.MaxTraceReferenceCharacters)
            || !CustomLoopArtifactIdentifier.IsValid(operation.Surface)
            || !Enum.IsDefined(operation.State))
        {
            throw new FormatException("Invocation-receipt retention operation failed canonical validation.");
        }

        RequireUtc(operation.RequestedAtUtc, nameof(operation.RequestedAtUtc));
        RequireUtc(operation.ReplayCutoffUtc, nameof(operation.ReplayCutoffUtc));
        RequireUtc(operation.OwnershipStartedAtUtc, nameof(operation.OwnershipStartedAtUtc));
        RequireUtc(operation.UpdatedAtUtc, nameof(operation.UpdatedAtUtc));
        if (operation.ReplayCutoffUtc != operation.RequestedAtUtc - CustomLoopInvocationReceiptRetentionPolicy.MinimumReplayDuration
            || operation.OwnershipStartedAtUtc < operation.RequestedAtUtc
            || operation.UpdatedAtUtc < operation.OwnershipStartedAtUtc
            || operation.Candidates is not { Length: > 0 and <= CustomLoopLimits.MaxInvocationOperationReceiptsPerWorkspace })
        {
            throw new FormatException("Invocation-receipt retention chronology or candidate count is invalid.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        CustomLoopInvocationReceiptRetentionCandidate? prior = null;
        foreach (var candidate in operation.Candidates)
        {
            if (candidate is null
                || !CustomLoopArtifactIdentifier.IsValid(candidate.OperationId, CustomLoopLimits.MaxMutationOperationIdCharacters)
                || !seen.Add(candidate.OperationId)
                || !IsHash(candidate.ArtifactHash)
                || candidate.ArtifactUtf8Bytes <= 0
                || candidate.ArtifactUtf8Bytes > CustomLoopLimits.MaxInvocationOperationUtf8Bytes)
            {
                throw new FormatException("Invocation-receipt retention contains an invalid or duplicate candidate.");
            }

            RequireUtc(candidate.CompletedAtUtc, nameof(candidate.CompletedAtUtc));
            if (candidate.CompletedAtUtc > operation.ReplayCutoffUtc
                || prior is not null && (candidate.CompletedAtUtc < prior.CompletedAtUtc
                    || candidate.CompletedAtUtc == prior.CompletedAtUtc && string.CompareOrdinal(candidate.OperationId, prior.OperationId) <= 0))
            {
                throw new FormatException("Invocation-receipt retention candidates are not canonical or cross the replay boundary.");
            }

            prior = candidate;
        }

        var committed = operation.State is CustomLoopInvocationReceiptRetentionOperationState.OutcomeCommitted
            or CustomLoopInvocationReceiptRetentionOperationState.OutcomeAuditStarted
            or CustomLoopInvocationReceiptRetentionOperationState.OutcomeAuditRecorded
            or CustomLoopInvocationReceiptRetentionOperationState.CommittedWithAuditWarning;
        var abandoned = operation.State is CustomLoopInvocationReceiptRetentionOperationState.AbandonedCandidateChanged
            or CustomLoopInvocationReceiptRetentionOperationState.AbandonedConflictAuditStarted
            or CustomLoopInvocationReceiptRetentionOperationState.AbandonedConflictAuditRecorded
            or CustomLoopInvocationReceiptRetentionOperationState.AbandonedWithAuditWarning;
        var expectedBytes = operation.Candidates.Sum(candidate => candidate.ArtifactUtf8Bytes);
        var invalidTotals = committed
            ? operation.DeletedReceiptCount != operation.Candidates.Length || operation.DeletedReceiptUtf8Bytes != expectedBytes
            : abandoned
                ? operation.DeletedReceiptCount < 0
                    || operation.DeletedReceiptCount >= operation.Candidates.Length
                    || operation.DeletedReceiptUtf8Bytes < 0
                    || operation.DeletedReceiptUtf8Bytes >= expectedBytes
                    || operation.DeletedReceiptCount == 0 != (operation.DeletedReceiptUtf8Bytes == 0)
                : operation.DeletedReceiptCount != 0 || operation.DeletedReceiptUtf8Bytes != 0;
        if (invalidTotals)
        {
            throw new FormatException("Invocation-receipt retention outcome totals do not match its durable candidates and state.");
        }
    }

    private static bool IsInsideOwnershipWindow(CustomLoopInvocationReceiptRetentionOperation operation, DateTimeOffset now)
    {
        return operation.OwnershipStartedAtUtc > now - CustomLoopInvocationReceiptRetentionPolicy.StaleRecoveryWindow;
    }

    private static bool IsInvocationOperationFileName(string fileName)
    {
        var operationId = Path.GetFileNameWithoutExtension(fileName);
        return string.Equals(Path.GetExtension(fileName), ".json", StringComparison.OrdinalIgnoreCase)
            && CustomLoopArtifactIdentifier.IsValid(operationId, CustomLoopLimits.MaxMutationOperationIdCharacters);
    }

    private static bool IsAtomicWriteTemp(string fileName, Func<string, bool> validTarget)
    {
        const string suffix = ".tmp";
        const int guidLength = 32;
        if (fileName.Length == 0 || fileName[0] != '.' || !fileName.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var guidStart = fileName.Length - suffix.Length - guidLength;
        if (guidStart <= 2 || fileName[guidStart - 1] != '.')
        {
            return false;
        }

        var target = fileName[1..(guidStart - 1)];
        var guid = fileName.Substring(guidStart, guidLength);
        return validTarget(target) && Guid.TryParseExact(guid, "N", out _);
    }

    private static string Hash(ReadOnlySpan<byte> content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left >= right ? left : right;

    private static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new FormatException($"{parameterName} must be a non-default UTC timestamp.");
        }
    }

    private static bool SameEnvelope(CustomLoopInvocationOperation left, CustomLoopInvocationOperation right)
    {
        return string.Equals(left.RequestHash, right.RequestHash, StringComparison.Ordinal)
            && string.Equals(left.OperationId, right.OperationId, StringComparison.Ordinal)
            && string.Equals(left.LoopId, right.LoopId, StringComparison.Ordinal)
            && left.ExpectedDefinitionVersion == right.ExpectedDefinitionVersion
            && string.Equals(left.ExpectedDefinitionHash, right.ExpectedDefinitionHash, StringComparison.Ordinal)
            && string.Equals(left.Actor, right.Actor, StringComparison.Ordinal)
            && string.Equals(left.Surface, right.Surface, StringComparison.Ordinal)
            && string.Equals(left.CurrentRoleId, right.CurrentRoleId, StringComparison.Ordinal)
            && string.Equals(left.InvocationPromptHash, right.InvocationPromptHash, StringComparison.Ordinal)
            && string.Equals(left.Provider, right.Provider, StringComparison.Ordinal)
            && string.Equals(left.Model, right.Model, StringComparison.Ordinal);
    }

    private static bool SameBinding(CustomLoopInvocationOperation left, CustomLoopInvocationOperation right)
    {
        return left.BindingState == right.BindingState
            && string.Equals(left.InvokingConversationId, right.InvokingConversationId, StringComparison.Ordinal)
            && string.Equals(left.ContextIdentityHash, right.ContextIdentityHash, StringComparison.Ordinal);
    }

    private static bool SameCompletedOperation(CustomLoopInvocationOperation left, CustomLoopInvocationOperation right)
    {
        return SameEnvelope(left, right)
            && SameBinding(left, right)
            && left.SchemaVersion == right.SchemaVersion
            && left.CreatedAtUtc == right.CreatedAtUtc
            && left.UpdatedAtUtc == right.UpdatedAtUtc
            && left.State == right.State
            && left.Outcome == right.Outcome
            && string.Equals(left.AdmissionStatus, right.AdmissionStatus, StringComparison.Ordinal)
            && string.Equals(left.RunId, right.RunId, StringComparison.Ordinal)
            && left.ValidationErrors.SequenceEqual(right.ValidationErrors)
            && string.Equals(left.Detail, right.Detail, StringComparison.Ordinal);
    }

    private static void Validate(CustomLoopInvocationOperation? operation, bool requirePending)
    {
        if (operation is null)
        {
            throw new FormatException("Custom-loop invocation operation cannot be null.");
        }

        var valid = operation.SchemaVersion == CustomLoopInvocationOperation.CurrentSchemaVersion
            && CustomLoopArtifactIdentifier.IsValid(operation.OperationId, CustomLoopLimits.MaxMutationOperationIdCharacters)
            && CustomLoopArtifactIdentifier.IsValid(operation.LoopId)
            && operation.ExpectedDefinitionVersion >= 1
            && IsHash(operation.ExpectedDefinitionHash)
            && IsBoundedText(operation.Actor, CustomLoopLimits.MaxTraceReferenceCharacters)
            && CustomLoopArtifactIdentifier.IsValid(operation.Surface)
            && CustomLoopArtifactIdentifier.IsValid(operation.CurrentRoleId)
            && IsHash(operation.InvocationPromptHash)
            && IsBoundedText(operation.Provider, CustomLoopLimits.MaxTraceReferenceCharacters)
            && (operation.Model is null || IsBoundedText(operation.Model, CustomLoopLimits.MaxTraceReferenceCharacters))
            && ValidBinding(operation)
            && IsHash(operation.RequestHash)
            && CustomLoopInvocationRequestHash.Matches(operation)
            && operation.CreatedAtUtc != default
            && operation.CreatedAtUtc.Offset == TimeSpan.Zero
            && operation.UpdatedAtUtc != default
            && operation.UpdatedAtUtc.Offset == TimeSpan.Zero
            && operation.UpdatedAtUtc >= operation.CreatedAtUtc
            && Enum.IsDefined(operation.State)
            && operation.State != CustomLoopInvocationOperationState.Unknown
            && Enum.IsDefined(operation.Outcome)
            && operation.Detail is { Length: > 0 and <= CustomLoopLimits.MaxRunDetailCharacters };
        valid = valid
            && operation.ValidationErrors is { Length: <= CustomLoopLimits.MaxInvocationValidationErrors }
            && operation.ValidationErrors.All(ValidValidationError);
        if (!valid)
        {
            throw new FormatException("Custom-loop invocation operation failed canonical validation.");
        }

        if (requirePending && (operation.State != CustomLoopInvocationOperationState.Pending
            || operation.Outcome != CustomLoopInvocationOutcome.Unknown
            || operation.AdmissionStatus is not { Length: 0 }
            || operation.RunId is not null
            || operation.ValidationErrors.Length != 0))
        {
            throw new FormatException("Pending custom-loop invocation operation contains completed outcome fields.");
        }

        if (operation.State == CustomLoopInvocationOperationState.Complete && !ValidCompletedOutcome(operation))
        {
            throw new FormatException("Completed custom-loop invocation operation is missing its durable outcome.");
        }
    }

    private static bool ValidCompletedOutcome(CustomLoopInvocationOperation operation)
    {
        if (operation.Outcome == CustomLoopInvocationOutcome.Unknown || !IsBoundedText(operation.AdmissionStatus, 120))
        {
            return false;
        }

        return operation.Outcome switch
        {
            CustomLoopInvocationOutcome.WorkspaceExecutionBusy => (operation.BindingState is CustomLoopInvocationBindingState.ConversationWorkspaceExecutionBusy or CustomLoopInvocationBindingState.CapturedContext) && operation.RunId is null && operation.ValidationErrors.Length == 0 && string.Equals(operation.AdmissionStatus, nameof(CustomLoopInvocationOutcome.WorkspaceExecutionBusy), StringComparison.Ordinal),
            CustomLoopInvocationOutcome.Admitted => operation.BindingState == CustomLoopInvocationBindingState.CapturedContext && CustomLoopArtifactIdentifier.IsValid(operation.RunId) && operation.ValidationErrors.Length == 0 && string.Equals(operation.AdmissionStatus, CustomLoopAdmissionStatusNames.Admitted, StringComparison.Ordinal),
            CustomLoopInvocationOutcome.Rejected => ValidRejectedOutcome(operation),
            _ => false
        };
    }

    private static bool ValidRejectedOutcome(CustomLoopInvocationOperation operation)
    {
        if (operation.BindingState == CustomLoopInvocationBindingState.Unbound)
        {
            return false;
        }

        var hasValidOptionalRun = operation.RunId is null || CustomLoopArtifactIdentifier.IsValid(operation.RunId);
        return operation.AdmissionStatus switch
        {
            CustomLoopAdmissionStatusNames.Invalid => operation.BindingState == CustomLoopInvocationBindingState.ConversationInvalid
                ? operation.RunId is null
                : operation.BindingState == CustomLoopInvocationBindingState.CapturedContext && hasValidOptionalRun,
            CustomLoopAdmissionStatusNames.Conflict => operation.BindingState == CustomLoopInvocationBindingState.CapturedContext && hasValidOptionalRun,
            CustomLoopAdmissionStatusNames.NonterminalRunExists => operation.BindingState == CustomLoopInvocationBindingState.CapturedContext && CustomLoopArtifactIdentifier.IsValid(operation.RunId),
            CustomLoopAdmissionStatusNames.LimitExceeded => operation.BindingState == CustomLoopInvocationBindingState.CapturedContext && operation.RunId is null,
            CustomLoopAdmissionStatusNames.NotFound => operation.BindingState is (CustomLoopInvocationBindingState.ConversationNotFound or CustomLoopInvocationBindingState.CapturedContextNotFound) && operation.RunId is null,
            CustomLoopAdmissionStatusNames.AuditUnavailable => operation.BindingState == CustomLoopInvocationBindingState.CapturedContext && hasValidOptionalRun,
            _ => false
        };
    }

    private static bool ValidBinding(CustomLoopInvocationOperation operation)
    {
        return operation.BindingState switch
        {
            CustomLoopInvocationBindingState.Unbound => operation.InvokingConversationId is null && operation.ContextIdentityHash is null,
            CustomLoopInvocationBindingState.ConversationNotFound => IsHash(operation.InvokingConversationId) && operation.ContextIdentityHash is null,
            CustomLoopInvocationBindingState.ConversationWorkspaceExecutionBusy => IsHash(operation.InvokingConversationId) && operation.ContextIdentityHash is null,
            CustomLoopInvocationBindingState.ConversationInvalid => IsHash(operation.InvokingConversationId) && operation.ContextIdentityHash is null,
            CustomLoopInvocationBindingState.CapturedContext => IsHash(operation.InvokingConversationId) && IsHash(operation.ContextIdentityHash),
            CustomLoopInvocationBindingState.CapturedContextNotFound => IsHash(operation.InvokingConversationId) && IsHash(operation.ContextIdentityHash),
            _ => false
        };
    }

    private static bool ValidValidationError(CustomLoopValidationError? error)
    {
        return error is not null
            && IsBoundedText(error.Code, CustomLoopLimits.MaxInvocationValidationErrorCodeCharacters)
            && IsBoundedText(error.Field, CustomLoopLimits.MaxInvocationValidationErrorFieldCharacters)
            && IsBoundedText(error.Message, CustomLoopLimits.MaxInvocationValidationErrorMessageCharacters);
    }

    private static bool IsHash(string? value)
    {
        return value is { Length: CustomLoopLimits.Sha256HexCharacters } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static bool IsBoundedText(string? value, int maximumLength)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= maximumLength
            && value.IsNormalized(NormalizationForm.FormC)
            && !value.Any(character => char.IsControl(character) || char.IsSurrogate(character));
    }
}
