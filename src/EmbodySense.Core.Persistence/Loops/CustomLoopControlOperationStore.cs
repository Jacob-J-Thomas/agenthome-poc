using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Application.Loops.Models;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.ReceiptRetention;
using EmbodySense.Core.Application.Loops.ReceiptRetention.Models;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Loops.Custom.Retention;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Retention;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Persistence.Loops;

/// <summary>
/// Persists bounded version-1 custom-loop control receipts with exclusive mutation ownership.
/// </summary>
/// <remarks>
/// One canonical JSON artifact is stored per operation identifier. Begin and completion transitions are serialized by an
/// in-process gate and cross-process file lease, then committed with a write-through atomic replace. Unknown fields, invalid
/// enum values, noncanonical state, missing ownership, or identity/path mismatches throw <see cref="FormatException"/>.
/// No compatibility reader or migration is provided for superseded POC shapes.
/// </remarks>
public sealed class CustomLoopControlOperationStore : ICustomLoopControlOperationStore, ICustomLoopReceiptRetentionPort
{
    private const long MaximumArtifactBytes = 64 * 1024;
    private const string ActiveCleanupJournalFileName = "active.json";
    private const int IntentAuditRecoveryTailLimit = 128;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _processGates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        MaxDepth = CustomLoopJsonDepthPolicy.ShallowReceiptMaximumDepth,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    private readonly string _root;
    private readonly string _retentionRoot;
    private readonly string _cleanupRoot;
    private readonly CustomLoopArtifactPathGuard _pathGuard;
    private readonly SemaphoreSlim _processGate;
    private readonly IAuditLog? _auditLog;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="CustomLoopControlOperationStore"/> type.
    /// </summary>
    /// <param name="paths">The paths.</param>
    /// <param name="auditLog">The optional governed audit sink required to execute cleanup beyond durable intent.</param>
    /// <param name="timeProvider">The clock used for exact replay boundaries and bounded cleanup ownership.</param>
    public CustomLoopControlOperationStore(WorkspacePaths paths, IAuditLog? auditLog = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _root = Path.GetFullPath(paths.CustomLoopControlOperationsPath);
        _retentionRoot = Path.GetFullPath(paths.CustomLoopReceiptRetentionPath);
        _cleanupRoot = Path.GetFullPath(paths.CustomLoopControlReceiptCleanupPath);
        _pathGuard = new CustomLoopArtifactPathGuard(paths.RootPath);
        _processGate = _processGates.GetOrAdd(_retentionRoot, _ => new SemaphoreSlim(1, 1));
        _auditLog = auditLog;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public CustomLoopReceiptArtifactClass ArtifactClass => CustomLoopReceiptArtifactClass.LifecycleControlReceipt;

    /// <summary>
    /// Creates, replays, or recovers the pending receipt for a control operation.
    /// </summary>
    /// <param name="operation">The canonical pending operation and request binding.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>
    /// A result reporting creation, replay, request conflict, or unproven ownership. A successful new or recovered pending
    /// operation carries the exclusive in-process ownership lease required by its bounded executor.
    /// </returns>
    public async Task<CustomLoopControlOperationStoreResult> BeginAsync(CustomLoopControlOperation operation, CancellationToken cancellationToken = default)
    {
        Validate(operation, requirePending: true);
        await _processGate.WaitAsync(cancellationToken);
        try
        {
            using var workspaceLock = _pathGuard.AcquireExclusiveMutationLock(_retentionRoot);
            _pathGuard.PrepareRoot(_root);
            ReclaimAbandonedInternalArtifactsUnderOwnership();
            var existing = await ReadIfExistsAsync(operation.OperationId, cancellationToken);
            var expired = await FindExpiredProofAsync(operation.OperationId, cancellationToken);
            ThrowIfRawAndCompactProofConflict(operation.OperationId, existing, expired);
            if (existing is not null)
            {
                if (!SameRequest(existing, operation))
                {
                    return new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.Conflict, existing);
                }

                if (existing.State == CustomLoopControlOperationState.Complete)
                {
                    return new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.Replayed, existing);
                }

                var replayLease = TryAcquireOperationOwnership(operation.OperationId);
                if (replayLease is null)
                {
                    return new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.OwnershipUnproven, existing);
                }

                try
                {
                    var recovered = WithOwnership(existing, replayLease, "The orphaned custom-loop control operation was claimed by a new bounded execution owner.");
                    await WriteAsync(recovered, cancellationToken);
                    return new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.Replayed, recovered, replayLease);
                }
                catch
                {
                    replayLease.Dispose();
                    throw;
                }
            }

            if (expired is not null)
            {
                // An expired proof deliberately reserves the idempotency identity even though exact replay is no longer
                // available. This remains distinct from an unknown identity so a caller cannot silently reuse it.
                return new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.Expired, null);
            }

            var journal = await ReadCleanupJournalAsync(cancellationToken);
            if (journal is not null && IsCleanupActive(journal.Stage))
            {
                if (IsInsideCleanupOwnershipWindow(journal, TrustedUtcNow()))
                {
                    return new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.OwnershipUnproven, null);
                }

                var recovery = await RecoverStaleCleanupBeforeLifecycleMutationAsync(journal, cancellationToken);
                journal = recovery.Journal;
                if (!recovery.AllowsLifecycleAdmission || IsCleanupActive(journal.Stage))
                {
                    return new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.OwnershipUnproven, null);
                }
            }

            var lease = TryAcquireOperationOwnership(operation.OperationId) ?? throw new InvalidOperationException("The new custom-loop control operation could not acquire its bounded execution ownership.");
            try
            {
                var owned = WithOwnership(operation, lease, operation.Detail);
                var json = SerializeBounded(owned);
                var usage = await ReadRawUsageAsync(cancellationToken, operation.OperationId);
                var budget = CustomLoopReceiptRetentionPolicy.GetBudget(ArtifactClass);
                if (!budget.CanAccountArtifacts(usage.Count, usage.Utf8Bytes, 1, Encoding.UTF8.GetByteCount(json), integrityPreservingCompletion: false))
                {
                    lease.Dispose();
                    return new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.QuotaExceeded, null);
                }

                await WriteAsync(owned, json, cancellationToken);
                return new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.Created, owned, lease);
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }
        finally
        {
            _processGate.Release();
        }
    }

    /// <summary>
    /// Loads the canonical receipt for an operation identifier.
    /// </summary>
    /// <param name="operationId">The operation ID.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The validated receipt, or <see langword="null"/> when no artifact exists.</returns>
    public async Task<CustomLoopControlOperation?> GetAsync(string operationId, CancellationToken cancellationToken = default)
    {
        var safeOperationId = CustomLoopArtifactIdentifier.Require(operationId, nameof(operationId), CustomLoopLimits.MaxMutationOperationIdCharacters);
        await _processGate.WaitAsync(cancellationToken);
        try
        {
            var exact = await ReadIfExistsAsync(safeOperationId, cancellationToken);
            var expired = await FindExpiredProofAsync(safeOperationId, cancellationToken);
            ThrowIfRawAndCompactProofConflict(safeOperationId, exact, expired);
            return exact;
        }
        finally
        {
            _processGate.Release();
        }
    }

    /// <summary>
    /// Commits the terminal receipt for an existing control operation with matching request and ownership.
    /// </summary>
    /// <param name="operation">The canonical completed operation, including the owner generation that began it.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A result reporting completion, idempotent replay, absence, or request/ownership conflict.</returns>
    public async Task<CustomLoopControlOperationStoreResult> CompleteAsync(CustomLoopControlOperation operation, CancellationToken cancellationToken = default)
    {
        Validate(operation, requirePending: false);
        if (operation.State != CustomLoopControlOperationState.Complete)
        {
            throw new ArgumentException("Completed control operation must have Complete state.", nameof(operation));
        }

        await _processGate.WaitAsync(cancellationToken);
        try
        {
            using var workspaceLock = _pathGuard.AcquireExclusiveMutationLock(_retentionRoot);
            ReclaimAbandonedInternalArtifactsUnderOwnership();
            var existing = await ReadIfExistsAsync(operation.OperationId, cancellationToken);
            var expired = await FindExpiredProofAsync(operation.OperationId, cancellationToken);
            ThrowIfRawAndCompactProofConflict(operation.OperationId, existing, expired);
            if (existing is null)
            {
                return new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.NotFound, null);
            }

            if (!SameRequest(existing, operation))
            {
                return new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.Conflict, existing);
            }

            if (existing.OwnerGenerationId is not null && !string.Equals(existing.OwnerGenerationId, operation.OwnerGenerationId, StringComparison.Ordinal))
            {
                return new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.Conflict, existing);
            }

            if (existing.State == CustomLoopControlOperationState.Complete)
            {
                return existing == operation
                    ? new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.Replayed, existing)
                    : new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.Conflict, existing);
            }

            var json = SerializeBounded(operation);
            var usage = await ReadRawUsageAsync(cancellationToken);
            var budget = CustomLoopReceiptRetentionPolicy.GetBudget(ArtifactClass);
            var existingBytes = Encoding.UTF8.GetByteCount(SerializeBounded(existing));
            if (!budget.CanAccountArtifacts(usage.Count - 1, usage.Utf8Bytes - existingBytes, 1, Encoding.UTF8.GetByteCount(json), integrityPreservingCompletion: true))
            {
                return new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.QuotaExceeded, existing);
            }

            await WriteAsync(operation, json, cancellationToken);
            return new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.Completed, operation);
        }
        finally
        {
            _processGate.Release();
        }
    }

    private async Task<CustomLoopControlOperation?> ReadIfExistsAsync(string operationId, CancellationToken cancellationToken)
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

        var bytes = await _pathGuard.ReadAllBytesAsync(_root, path, MaximumArtifactBytes, "Custom-loop control operation", cancellationToken);
        CustomLoopJsonDepthPolicy.ValidatePersistedJsonDepth(bytes, _jsonOptions.MaxDepth, "Custom-loop control operation", path);
        CustomLoopControlOperation? operation;
        try
        {
            operation = JsonSerializer.Deserialize<CustomLoopControlOperation>(bytes, _jsonOptions);
        }
        catch (JsonException exception)
        {
            throw new FormatException($"Custom-loop control operation `{path}` is invalid JSON.", exception);
        }

        Validate(operation, requirePending: operation?.State == CustomLoopControlOperationState.Pending);
        if (operation!.OwnerGenerationId is null || operation.OwnerProcessId is null || operation.OwnerAcquiredAtUtc is null)
        {
            throw new FormatException("Persisted custom-loop control operation is missing ownership metadata.");
        }

        if (!string.Equals(operation!.OperationId, operationId, StringComparison.Ordinal))
        {
            throw new FormatException($"Custom-loop control operation filename `{operationId}` does not match embedded id `{operation.OperationId}`.");
        }

        return operation;
    }

    private async Task WriteAsync(CustomLoopControlOperation operation, CancellationToken cancellationToken)
    {
        await WriteAsync(operation, SerializeBounded(operation), cancellationToken);
    }

    private async Task WriteAsync(CustomLoopControlOperation operation, string json, CancellationToken cancellationToken)
    {
        var path = _pathGuard.GetFilePath(_root, operation.OperationId + ".json");
        await _pathGuard.WriteTextAtomicallyAsync(_root, path, json, cancellationToken);
    }

    private static string SerializeBounded(CustomLoopControlOperation operation)
    {
        try
        {
            var json = JsonSerializer.Serialize(operation, _jsonOptions);
            if (Encoding.UTF8.GetByteCount(json) > MaximumArtifactBytes)
            {
                throw new ArgumentException($"Custom-loop control operation exceeds {MaximumArtifactBytes} UTF-8 bytes.", nameof(operation));
            }

            return json;
        }
        catch (JsonException exception)
        {
            throw CustomLoopJsonDepthPolicy.SerializationDepthException("Custom-loop control operation", _jsonOptions.MaxDepth, exception);
        }
    }

    /// <inheritdoc />
    public async Task<CustomLoopReceiptClassPosture> InspectAsync(CancellationToken cancellationToken = default)
    {
        await _processGate.WaitAsync(cancellationToken);
        try
        {
            var categories = CreateEmptyUsage();
            var detail = "Lifecycle-control receipts are within their bounded retention posture.";
            CustomLoopReceiptCleanupBlockReason blockReason = CustomLoopReceiptCleanupBlockReason.None;
            CustomLoopReceiptQuotaExhaustionReason exhaustionReason = CustomLoopReceiptQuotaExhaustionReason.None;
            DateTimeOffset? oldestExpiry = null;
            DateTimeOffset? newestExpiry = null;
            try
            {
                var now = TrustedUtcNow();
                var artifacts = await ReadAllOperationArtifactsAsync(cancellationToken);
                foreach (var artifact in artifacts)
                {
                    var category = Classify(artifact.Operation, now);
                    AddUsage(categories, category, artifact.Bytes.Length);
                    if (category == CustomLoopReceiptArtifactCategory.Live)
                    {
                        var expiry = artifact.Operation.UpdatedAtUtc + CustomLoopReceiptRetentionPolicy.ExactReplayDuration;
                        oldestExpiry = oldestExpiry is null || expiry < oldestExpiry ? expiry : oldestExpiry;
                        newestExpiry = newestExpiry is null || expiry > newestExpiry ? expiry : newestExpiry;
                    }
                }

                var ledger = await ReadProofLedgerAsync(cancellationToken);
                if (ledger is not null)
                {
                    var proofCount = 0;
                    foreach (var proof in ledger.ExpiredOperations.Where(item => item.ArtifactClass == ArtifactClass).OrderBy(item => item.OperationId, StringComparer.Ordinal))
                    {
                        AddUsage(categories, CustomLoopReceiptArtifactCategory.ExpiredIdempotency, CustomLoopReceiptRetentionContractCodec.MeasureExpiredOperationProofUtf8Bytes(proof) + (proofCount == 0 ? 0 : 1));
                        proofCount++;
                    }
                }

                var journal = await ReadCleanupJournalAsync(cancellationToken);
                if (journal is not null && IsCleanupActive(journal.Stage))
                {
                    blockReason = CustomLoopReceiptCleanupBlockReason.OwnershipUnresolved;
                    detail = "A lifecycle-control receipt cleanup journal is still inside its bounded ownership or recovery window.";
                }

                var usage = SummarizeUsage(categories);
                exhaustionReason = DetermineExhaustionReason(usage.ArtifactCount, usage.ArtifactUtf8Bytes, usage.ProofCount, usage.ProofUtf8Bytes);
                if (blockReason == CustomLoopReceiptCleanupBlockReason.None)
                {
                    blockReason = DetermineBlockReason(categories, exhaustionReason);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                AddUsage(categories, CustomLoopReceiptArtifactCategory.Corrupt, 1);
                blockReason = CustomLoopReceiptCleanupBlockReason.CorruptEvidence;
                detail = $"Lifecycle-control receipt retention evidence could not be classified safely: {exception.GetType().Name}.";
            }

            var posture = new CustomLoopReceiptClassPosture(ArtifactClass, CustomLoopReceiptRetentionPolicy.GetBudget(ArtifactClass), categories.Values.OrderBy(item => item.Category).ToImmutableArray(), oldestExpiry, newestExpiry, exhaustionReason, blockReason, detail);
            CustomLoopReceiptRetentionContractValidator.ValidateClassPosture(posture);
            return posture;
        }
        finally
        {
            _processGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<CustomLoopReceiptOperationLookupResult> LookupOperationAsync(string operationId, CancellationToken cancellationToken = default)
    {
        var safeOperationId = CustomLoopArtifactIdentifier.Require(operationId, nameof(operationId), CustomLoopLimits.MaxMutationOperationIdCharacters);
        await _processGate.WaitAsync(cancellationToken);
        try
        {
            var exact = await ReadIfExistsAsync(safeOperationId, cancellationToken);
            var expired = await FindExpiredProofAsync(safeOperationId, cancellationToken);
            ThrowIfRawAndCompactProofConflict(safeOperationId, exact, expired);
            if (exact is not null)
            {
                return new CustomLoopReceiptOperationLookupResult(ArtifactClass, safeOperationId, CustomLoopReceiptOperationLookupStatus.Exact, null, "The complete lifecycle-control receipt remains available for exact replay.");
            }

            return expired is null
                ? new CustomLoopReceiptOperationLookupResult(ArtifactClass, safeOperationId, CustomLoopReceiptOperationLookupStatus.Unknown, null, "No lifecycle-control receipt or compact expiry proof recognizes this operation identity.")
                : new CustomLoopReceiptOperationLookupResult(ArtifactClass, safeOperationId, CustomLoopReceiptOperationLookupStatus.Expired, expired, "The lifecycle-control receipt expired after its exact replay horizon; its idempotency identity remains reserved by compact proof.");
        }
        finally
        {
            _processGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<CustomLoopReceiptCleanupResult> CleanupAsync(CustomLoopReceiptCleanupCommand command, CancellationToken cancellationToken = default)
    {
        CustomLoopReceiptCleanupRequest request;
        try
        {
            request = CustomLoopReceiptCleanupRequestFactory.Create(command, TrustedUtcNow());
        }
        catch (ArgumentException exception)
        {
            return CleanupResult(CustomLoopReceiptCleanupStatus.Invalid, null, CustomLoopReceiptQuotaExhaustionReason.None, CustomLoopReceiptCleanupBlockReason.None, exception.Message);
        }

        if (request.ArtifactClass != ArtifactClass)
        {
            return CleanupResult(CustomLoopReceiptCleanupStatus.Invalid, null, CustomLoopReceiptQuotaExhaustionReason.None, CustomLoopReceiptCleanupBlockReason.None, "Lifecycle-control retention cannot process a different receipt artifact class.");
        }

        await _processGate.WaitAsync(cancellationToken);
        try
        {
            FileStream workspaceLock;
            try
            {
                workspaceLock = _pathGuard.AcquireExclusiveMutationLock(_retentionRoot);
            }
            catch (InvalidOperationException)
            {
                return CleanupResult(CustomLoopReceiptCleanupStatus.OperationInProgress, null, CustomLoopReceiptQuotaExhaustionReason.None, CustomLoopReceiptCleanupBlockReason.OwnershipUnresolved, "Another process holds the lifecycle-control receipt retention mutation lease.");
            }

            using (workspaceLock)
            {
                ReclaimAbandonedInternalArtifactsUnderOwnership();
                return await CleanupUnderOwnershipAsync(request, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return CleanupResult(CustomLoopReceiptCleanupStatus.Corrupt, null, CustomLoopReceiptQuotaExhaustionReason.None, CustomLoopReceiptCleanupBlockReason.CorruptEvidence, $"Lifecycle-control receipt cleanup failed closed on corrupt evidence: {exception.Message}");
        }
        finally
        {
            _processGate.Release();
        }
    }

    private async Task<CustomLoopReceiptCleanupResult> CleanupUnderOwnershipAsync(CustomLoopReceiptCleanupRequest request, CancellationToken cancellationToken)
    {
        var existing = await ReadCleanupJournalAsync(cancellationToken);
        if (existing is not null && string.Equals(existing.Request.OperationId, request.OperationId, StringComparison.Ordinal))
        {
            if (!MatchesCleanupCommand(existing.Request, request))
            {
                return CleanupResult(CustomLoopReceiptCleanupStatus.Invalid, existing, CustomLoopReceiptQuotaExhaustionReason.None, CustomLoopReceiptCleanupBlockReason.None, "The lifecycle-control receipt cleanup operation ID is already bound to different request content.");
            }

            request = existing.Request;
        }

        if (existing is not null)
        {
            if (string.Equals(existing.RequestHash, CustomLoopReceiptRetentionContractCodec.ComputeCleanupRequestHash(request), StringComparison.Ordinal))
            {
                if (IsCleanupTerminal(existing.Stage))
                {
                    return ResultForJournal(existing, replay: true);
                }

                return IsInsideCleanupOwnershipWindow(existing, TrustedUtcNow())
                    ? CleanupResult(CustomLoopReceiptCleanupStatus.OperationInProgress, existing, CustomLoopReceiptQuotaExhaustionReason.None, CustomLoopReceiptCleanupBlockReason.OwnershipUnresolved, "Another process owns lifecycle-control receipt cleanup or its bounded crash-recovery window.")
                    : await ResumeCleanupAsync(Reown(existing), cancellationToken);
            }

            if (IsCleanupActive(existing.Stage))
            {
                if (IsInsideCleanupOwnershipWindow(existing, TrustedUtcNow()))
                {
                    return CleanupResult(CustomLoopReceiptCleanupStatus.OperationInProgress, existing, CustomLoopReceiptQuotaExhaustionReason.None, CustomLoopReceiptCleanupBlockReason.OwnershipUnresolved, "Another process owns lifecycle-control receipt cleanup or its bounded crash-recovery window.");
                }

                return await ResumeCleanupAsync(Reown(existing), cancellationToken);
            }
        }

        IReadOnlyList<(CustomLoopControlOperation Operation, byte[] Bytes, string Path)> artifacts;
        try
        {
            artifacts = await ReadAllOperationArtifactsAsync(cancellationToken);
        }
        catch (FormatException exception)
        {
            return CleanupResult(CustomLoopReceiptCleanupStatus.Corrupt, null, CustomLoopReceiptQuotaExhaustionReason.None, CustomLoopReceiptCleanupBlockReason.CorruptEvidence, $"Lifecycle-control receipt cleanup preserved every artifact because classification failed: {exception.Message}");
        }

        var ledger = await ReadProofLedgerAsync(cancellationToken);
        var now = TrustedUtcNow();
        if (IsFutureCleanupRequest(request, now))
        {
            return CleanupResult(CustomLoopReceiptCleanupStatus.Invalid, null, CustomLoopReceiptQuotaExhaustionReason.None, CustomLoopReceiptCleanupBlockReason.None, "Lifecycle-control receipt cleanup rejected request time or replay cutoff that is ahead of the trusted retention clock.");
        }

        var candidates = SelectCandidates(artifacts, ledger, request, now);
        if (candidates is null)
        {
            return CleanupResult(CustomLoopReceiptCleanupStatus.Degraded, null, CustomLoopReceiptQuotaExhaustionReason.None, CustomLoopReceiptCleanupBlockReason.AmbiguousEvidence, "Lifecycle-control receipt cleanup found a raw receipt whose idempotency identity is already represented by compact proof; no evidence was removed.");
        }

        if (candidates.Count == 0)
        {
            var nothingEligible = CreateJournal(request, ImmutableArray<CustomLoopReceiptCleanupCandidate>.Empty, CustomLoopReceiptCleanupStage.Completed, CustomLoopReceiptCleanupOutcome.NothingEligible, null, 0, 0, "No complete audited lifecycle-control receipt is outside the exact replay horizon.", now);
            await WriteCleanupJournalAsync(nothingEligible, cancellationToken);
            return CleanupResult(CustomLoopReceiptCleanupStatus.NothingEligible, nothingEligible, CustomLoopReceiptQuotaExhaustionReason.None, DetermineNoCandidateBlockReason(artifacts, now), nothingEligible.Detail);
        }

        var candidateArray = candidates.OrderBy(item => item.ArtifactId, StringComparer.Ordinal).ToImmutableArray();
        var proofUsage = MeasureProofUsage(ledger, candidateArray);
        if (proofUsage.ExhaustionReason != CustomLoopReceiptQuotaExhaustionReason.None)
        {
            return CleanupResult(CustomLoopReceiptCleanupStatus.QuotaExhausted, null, proofUsage.ExhaustionReason, CustomLoopReceiptCleanupBlockReason.ProofCapacityExhausted, "Lifecycle-control receipt cleanup preserved every selected receipt because compact expiry proof capacity is exhausted.");
        }

        var intent = CreateJournal(request, candidateArray, CustomLoopReceiptCleanupStage.IntentPersisted, CustomLoopReceiptCleanupOutcome.Unknown, null, 0, 0, "Expired lifecycle-control receipts were selected under bounded cross-process ownership.", now);
        await WriteCleanupJournalAsync(intent, cancellationToken);
        return await ResumeCleanupAsync(intent, cancellationToken);
    }

    private static bool MatchesCleanupCommand(CustomLoopReceiptCleanupRequest persisted, CustomLoopReceiptCleanupRequest candidate)
    {
        return persisted.SchemaVersion == candidate.SchemaVersion
            && persisted.ArtifactClass == candidate.ArtifactClass
            && string.Equals(persisted.OperationId, candidate.OperationId, StringComparison.Ordinal)
            && string.Equals(persisted.Actor, candidate.Actor, StringComparison.Ordinal)
            && string.Equals(persisted.Surface, candidate.Surface, StringComparison.Ordinal)
            && persisted.MaximumArtifactCount == candidate.MaximumArtifactCount
            && persisted.MaximumArtifactUtf8Bytes == candidate.MaximumArtifactUtf8Bytes;
    }

    private async Task<CustomLoopReceiptCleanupResult> ResumeCleanupAsync(CustomLoopReceiptCleanupJournal journal, CancellationToken cancellationToken)
    {
        if (journal.Stage == CustomLoopReceiptCleanupStage.IntentPersisted)
        {
            if (_auditLog is null)
            {
                return CleanupResult(CustomLoopReceiptCleanupStatus.AuditUnavailable, journal, CustomLoopReceiptQuotaExhaustionReason.None, CustomLoopReceiptCleanupBlockReason.AuditUnavailable, "Lifecycle-control receipt cleanup persisted intent but no governed audit sink is available, so no proof or raw receipt was changed.");
            }

            journal = Advance(journal, CustomLoopReceiptCleanupStage.IntentAuditStarted, CustomLoopReceiptCleanupOutcome.Unknown, null, 0, 0, "Lifecycle-control receipt cleanup durably started its one bounded intent-audit append.");
            await WriteCleanupJournalAsync(journal, cancellationToken);
            return await AppendIntentAuditAsync(journal, cancellationToken);
        }

        if (journal.Stage == CustomLoopReceiptCleanupStage.IntentAuditStarted)
        {
            return await RecoverStartedIntentAuditAsync(journal, cancellationToken);
        }

        if (journal.Stage == CustomLoopReceiptCleanupStage.IntentAuditRecorded)
        {
            var checkedCandidates = await RevalidateCandidatesAsync(journal, cancellationToken);
            if (!checkedCandidates.AllMatch)
            {
                var conflict = Advance(journal, CustomLoopReceiptCleanupStage.AbandonedConflict, CustomLoopReceiptCleanupOutcome.Conflict, null, 0, 0, "A selected lifecycle-control receipt changed or disappeared before compact proof was committed; no receipt was removed.");
                await WriteCleanupJournalAsync(conflict, cancellationToken);
                return CleanupResult(CustomLoopReceiptCleanupStatus.CleanupConflict, conflict, CustomLoopReceiptQuotaExhaustionReason.None, CustomLoopReceiptCleanupBlockReason.CleanupConflict, conflict.Detail);
            }

            var currentLedger = await ReadProofLedgerAsync(cancellationToken);
            var proofResolution = ResolveProofAdditions(currentLedger, journal.Candidates);
            if (!proofResolution.AllEquivalent)
            {
                var degraded = Advance(journal, CustomLoopReceiptCleanupStage.Degraded, CustomLoopReceiptCleanupOutcome.Degraded, null, 0, 0, "Compact proof already reserves a selected lifecycle-control operation identity with different immutable evidence; no raw receipt was removed.");
                await WriteCleanupJournalAsync(degraded, cancellationToken);
                return CleanupResult(CustomLoopReceiptCleanupStatus.Degraded, degraded, CustomLoopReceiptQuotaExhaustionReason.None, CustomLoopReceiptCleanupBlockReason.AmbiguousEvidence, degraded.Detail);
            }

            var proofUsage = MeasureProofUsage(currentLedger, proofResolution.MissingCandidates);
            if (proofUsage.ExhaustionReason != CustomLoopReceiptQuotaExhaustionReason.None)
            {
                return CleanupResult(CustomLoopReceiptCleanupStatus.QuotaExhausted, journal, proofUsage.ExhaustionReason, CustomLoopReceiptCleanupBlockReason.ProofCapacityExhausted, "Lifecycle-control receipt cleanup cannot replace its selected raw receipts because compact proof capacity is exhausted.");
            }

            var replacement = proofResolution.MissingCandidates.Length == 0
                ? currentLedger
                : AppendProofs(currentLedger, proofResolution.MissingCandidates, MonotonicTrustedUtcNow(journal.UpdatedAtUtc));
            if (replacement is null)
            {
                var corrupt = Advance(journal, CustomLoopReceiptCleanupStage.Degraded, CustomLoopReceiptCleanupOutcome.Corrupt, null, 0, 0, "Compact proof recovery could not resolve a replacement ledger; no raw lifecycle-control receipt was removed.");
                await WriteCleanupJournalAsync(corrupt, cancellationToken);
                return CleanupResult(CustomLoopReceiptCleanupStatus.Corrupt, corrupt, CustomLoopReceiptQuotaExhaustionReason.None, CustomLoopReceiptCleanupBlockReason.CorruptEvidence, corrupt.Detail);
            }

            var ledgerHash = CustomLoopReceiptRetentionContractCodec.ComputeProofLedgerHash(replacement);
            if (proofResolution.MissingCandidates.Length > 0)
            {
                await WriteProofLedgerAsync(replacement, cancellationToken);
            }

            var persistedLedger = await ReadProofLedgerAsync(cancellationToken);
            if (persistedLedger is null || !CustomLoopReceiptRetentionContractCodec.ProofLedgersEqual(persistedLedger, replacement) || !string.Equals(CustomLoopReceiptRetentionContractCodec.ComputeProofLedgerHash(persistedLedger), ledgerHash, StringComparison.Ordinal))
            {
                var corrupt = Advance(journal, CustomLoopReceiptCleanupStage.Degraded, CustomLoopReceiptCleanupOutcome.Corrupt, null, 0, 0, "Compact proof ledger write could not be revalidated; no raw lifecycle-control receipt was removed.");
                await WriteCleanupJournalAsync(corrupt, cancellationToken);
                return CleanupResult(CustomLoopReceiptCleanupStatus.Corrupt, corrupt, CustomLoopReceiptQuotaExhaustionReason.None, CustomLoopReceiptCleanupBlockReason.CorruptEvidence, corrupt.Detail);
            }

            journal = Advance(journal, CustomLoopReceiptCleanupStage.ProofLedgerWritten, CustomLoopReceiptCleanupOutcome.Unknown, ledgerHash, 0, 0, "Compact lifecycle-control expiry proof was written and verified before raw receipt removal.");
            await WriteCleanupJournalAsync(journal, cancellationToken);
        }

        if (journal.Stage == CustomLoopReceiptCleanupStage.ProofLedgerWritten)
        {
            var ledger = await ReadProofLedgerAsync(cancellationToken);
            if (ledger is null || !string.Equals(CustomLoopReceiptRetentionContractCodec.ComputeProofLedgerHash(ledger), journal.ProofLedgerHash, StringComparison.Ordinal))
            {
                var corrupt = Advance(journal, CustomLoopReceiptCleanupStage.Degraded, CustomLoopReceiptCleanupOutcome.Corrupt, journal.ProofLedgerHash, journal.RemovedArtifactCount, journal.RemovedArtifactUtf8Bytes, "The durable compact proof ledger no longer matches the cleanup journal; no additional raw receipt was removed.");
                await WriteCleanupJournalAsync(corrupt, cancellationToken);
                return CleanupResult(CustomLoopReceiptCleanupStatus.Corrupt, corrupt, CustomLoopReceiptQuotaExhaustionReason.None, CustomLoopReceiptCleanupBlockReason.CorruptEvidence, corrupt.Detail);
            }

            var removalProgress = await ReconcileRemovalProgressAsync(journal, cancellationToken);
            if (!removalProgress.IsCanonical)
            {
                var degraded = Advance(journal, CustomLoopReceiptCleanupStage.Degraded, CustomLoopReceiptCleanupOutcome.Degraded, journal.ProofLedgerHash, removalProgress.AttributedRemovedCount, removalProgress.AttributedRemovedBytes, "Lifecycle-control receipt state no longer forms the canonical removal prefix after proof commit; exact attributable progress is preserved and cleanup requires review.");
                await WriteCleanupJournalAsync(degraded, cancellationToken);
                return CleanupResult(CustomLoopReceiptCleanupStatus.Degraded, degraded, CustomLoopReceiptQuotaExhaustionReason.None, CustomLoopReceiptCleanupBlockReason.AmbiguousEvidence, degraded.Detail);
            }

            if (removalProgress.AttributedRemovedCount > journal.RemovedArtifactCount)
            {
                journal = Advance(journal, CustomLoopReceiptCleanupStage.ProofLedgerWritten, CustomLoopReceiptCleanupOutcome.Unknown, journal.ProofLedgerHash, removalProgress.AttributedRemovedCount, removalProgress.AttributedRemovedBytes, "A canonical missing receipt prefix was reconstructed as exact attributed progress after an interrupted removal write.");
                await WriteCleanupJournalAsync(journal, cancellationToken);
            }

            var canonicalCandidates = journal.Candidates.OrderBy(item => item.ArtifactId, StringComparer.Ordinal).ToArray();
            var removedCount = journal.RemovedArtifactCount;
            var removedBytes = journal.RemovedArtifactUtf8Bytes;
            foreach (var candidate in canonicalCandidates.Skip(removedCount))
            {
                if (!TryDeleteInactiveOperationOwnerLock(candidate.ArtifactId))
                {
                    var degraded = Advance(journal, CustomLoopReceiptCleanupStage.Degraded, CustomLoopReceiptCleanupOutcome.Degraded, journal.ProofLedgerHash, removedCount, removedBytes, "A selected lifecycle-control receipt still has active execution ownership after compact proof was committed; no additional raw receipt was removed.");
                    await WriteCleanupJournalAsync(degraded, cancellationToken);
                    return CleanupResult(CustomLoopReceiptCleanupStatus.Degraded, degraded, CustomLoopReceiptQuotaExhaustionReason.None, CustomLoopReceiptCleanupBlockReason.OwnershipUnresolved, degraded.Detail);
                }

                _pathGuard.DeleteFile(_root, _pathGuard.GetFilePath(_root, candidate.ArtifactId + ".json"));
                removedCount++;
                removedBytes = checked(removedBytes + candidate.ArtifactUtf8Bytes);
                journal = Advance(journal, CustomLoopReceiptCleanupStage.ProofLedgerWritten, CustomLoopReceiptCleanupOutcome.Unknown, journal.ProofLedgerHash, removedCount, removedBytes, "One canonical lifecycle-control receipt removal is durably attributed within the immutable cleanup batch.");
                await WriteCleanupJournalAsync(journal, cancellationToken);
            }

            var removed = Advance(journal, CustomLoopReceiptCleanupStage.ArtifactsRemoved, CustomLoopReceiptCleanupOutcome.Unknown, journal.ProofLedgerHash, removedCount, removedBytes, "Every selected lifecycle-control receipt was hash-revalidated and removed after its replacement proof was durable.");
            await WriteCleanupJournalAsync(removed, cancellationToken);
            journal = removed;
        }

        if (journal.Stage == CustomLoopReceiptCleanupStage.ArtifactsRemoved)
        {
            var auditStarted = Advance(journal, CustomLoopReceiptCleanupStage.OutcomeAuditStarted, CustomLoopReceiptCleanupOutcome.Unknown, journal.ProofLedgerHash, journal.RemovedArtifactCount, journal.RemovedArtifactUtf8Bytes, "Lifecycle-control receipt cleanup committed removal and started its one bounded outcome-audit attempt.");
            await WriteCleanupJournalAsync(auditStarted, cancellationToken);
            journal = auditStarted;
            if (_auditLog is null)
            {
                return await CommitAuditWarningAsync(journal, cancellationToken, "Lifecycle-control receipt cleanup committed raw removal but no governed outcome audit sink is available.");
            }

            try
            {
                await _auditLog.AppendAsync(CreateRetentionAudit(journal, AuditSchema.Actions.LoopControlReceiptRetentionOutcome, AuditSchema.Outcomes.Succeeded, "Expired lifecycle-control receipts were compacted into durable idempotency proof."), cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return await CommitAuditWarningAsync(journal, cancellationToken, $"Lifecycle-control receipt cleanup committed raw removal but outcome audit could not be appended: {exception.GetType().Name}.");
            }

            var completed = Advance(journal, CustomLoopReceiptCleanupStage.Completed, CustomLoopReceiptCleanupOutcome.Succeeded, journal.ProofLedgerHash, journal.RemovedArtifactCount, journal.RemovedArtifactUtf8Bytes, "Lifecycle-control receipt cleanup and its outcome audit are durable.");
            await WriteCleanupJournalAsync(completed, cancellationToken);
            return CleanupResult(CustomLoopReceiptCleanupStatus.Pruned, completed, CustomLoopReceiptQuotaExhaustionReason.None, CustomLoopReceiptCleanupBlockReason.None, completed.Detail);
        }

        if (journal.Stage == CustomLoopReceiptCleanupStage.OutcomeAuditStarted)
        {
            return await CommitAuditWarningAsync(journal, cancellationToken, "Lifecycle-control receipt cleanup outcome audit was already started before restart and will not be repeated without durable confirmation.");
        }

        return ResultForJournal(journal, replay: false);
    }

    private async Task<(CustomLoopReceiptCleanupJournal Journal, bool AllowsLifecycleAdmission)> RecoverStaleCleanupBeforeLifecycleMutationAsync(CustomLoopReceiptCleanupJournal journal, CancellationToken cancellationToken)
    {
        var reowned = Reown(journal);
        var recovered = await ResumeCleanupAsync(reowned, cancellationToken);
        var recoveredJournal = recovered.Journal ?? reowned;
        if (!IsCleanupActive(recoveredJournal.Stage))
        {
            var allowsLifecycleAdmission = recovered.Status is CustomLoopReceiptCleanupStatus.Pruned
                or CustomLoopReceiptCleanupStatus.Replayed
                or CustomLoopReceiptCleanupStatus.NothingEligible
                or CustomLoopReceiptCleanupStatus.CommittedWithAuditWarning
                or CustomLoopReceiptCleanupStatus.AuditUnavailable
                or CustomLoopReceiptCleanupStatus.CleanupConflict;
            return (recoveredJournal, allowsLifecycleAdmission);
        }

        if (recoveredJournal.Stage is CustomLoopReceiptCleanupStage.IntentPersisted or CustomLoopReceiptCleanupStage.IntentAuditRecorded)
        {
            var degraded = Advance(recoveredJournal, CustomLoopReceiptCleanupStage.Degraded, CustomLoopReceiptCleanupOutcome.Degraded, null, 0, 0, "A stale lifecycle-control receipt cleanup could not advance before compact proof commit; raw receipts were preserved and normal lifecycle admission may continue within its bounded quota.");
            await WriteCleanupJournalAsync(degraded, cancellationToken);
            return (degraded, true);
        }

        return (recoveredJournal, false);
    }

    private async Task<CustomLoopReceiptCleanupResult> AppendIntentAuditAsync(CustomLoopReceiptCleanupJournal journal, CancellationToken cancellationToken)
    {
        try
        {
            await _auditLog!.AppendAsync(CreateRetentionAudit(journal, AuditSchema.Actions.LoopControlReceiptRetentionIntent, AuditSchema.Outcomes.Requested, "Expired lifecycle-control receipts were selected for bounded cleanup."), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return CleanupResult(CustomLoopReceiptCleanupStatus.AuditUnavailable, journal, CustomLoopReceiptQuotaExhaustionReason.None, CustomLoopReceiptCleanupBlockReason.AuditUnavailable, $"Lifecycle-control receipt cleanup preserved every artifact because intent audit could not be confirmed: {exception.GetType().Name}.");
        }

        var recorded = Advance(journal, CustomLoopReceiptCleanupStage.IntentAuditRecorded, CustomLoopReceiptCleanupOutcome.Unknown, null, 0, 0, "Lifecycle-control receipt cleanup intent audit is durable.");
        await WriteCleanupJournalAsync(recorded, cancellationToken);
        return await ResumeCleanupAsync(recorded, cancellationToken);
    }

    private async Task<CustomLoopReceiptCleanupResult> RecoverStartedIntentAuditAsync(CustomLoopReceiptCleanupJournal journal, CancellationToken cancellationToken)
    {
        if (await IsIntentAuditRecordedAsync(journal, cancellationToken))
        {
            var recorded = Advance(journal, CustomLoopReceiptCleanupStage.IntentAuditRecorded, CustomLoopReceiptCleanupOutcome.Unknown, null, 0, 0, "Lifecycle-control receipt cleanup recovered its confirmed intent audit without appending duplicate evidence.");
            await WriteCleanupJournalAsync(recorded, cancellationToken);
            return await ResumeCleanupAsync(recorded, cancellationToken);
        }

        var degraded = Advance(journal, CustomLoopReceiptCleanupStage.Degraded, CustomLoopReceiptCleanupOutcome.AuditUnavailable, null, 0, 0, "A prior lifecycle-control receipt cleanup intent-audit append may have completed, but bounded audit evidence cannot confirm it; raw receipts were preserved and the append will not be repeated.");
        await WriteCleanupJournalAsync(degraded, cancellationToken);
        return CleanupResult(CustomLoopReceiptCleanupStatus.AuditUnavailable, degraded, CustomLoopReceiptQuotaExhaustionReason.None, CustomLoopReceiptCleanupBlockReason.AuditUnavailable, degraded.Detail);
    }

    private async Task<bool> IsIntentAuditRecordedAsync(CustomLoopReceiptCleanupJournal journal, CancellationToken cancellationToken)
    {
        if (_auditLog is null)
        {
            return false;
        }

        try
        {
            var auditEvents = await _auditLog.ReadTailAsync(IntentAuditRecoveryTailLimit, cancellationToken);
            return auditEvents.Any(auditEvent => string.Equals(auditEvent.Actor, journal.Request.Actor, StringComparison.Ordinal)
                && string.Equals(auditEvent.Action, AuditSchema.Actions.LoopControlReceiptRetentionIntent, StringComparison.Ordinal)
                && string.Equals(auditEvent.Target, journal.Request.OperationId, StringComparison.Ordinal)
                && string.Equals(auditEvent.Outcome, AuditSchema.Outcomes.Requested, StringComparison.Ordinal)
                && HasAuditMetadataValue(auditEvent, "requestHash", journal.RequestHash));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    private static bool HasAuditMetadataValue(AuditEvent auditEvent, string key, string expected)
    {
        if (!auditEvent.Metadata.TryGetValue(key, out var value))
        {
            return false;
        }

        var actual = value is JsonElement { ValueKind: JsonValueKind.String } element ? element.GetString() : value as string;
        return string.Equals(actual, expected, StringComparison.Ordinal);
    }

    private async Task<CustomLoopReceiptCleanupResult> CommitAuditWarningAsync(CustomLoopReceiptCleanupJournal journal, CancellationToken cancellationToken, string detail)
    {
        var warning = Advance(journal, CustomLoopReceiptCleanupStage.CommittedWithAuditWarning, CustomLoopReceiptCleanupOutcome.AuditUnavailable, journal.ProofLedgerHash, journal.RemovedArtifactCount, journal.RemovedArtifactUtf8Bytes, detail);
        await WriteCleanupJournalAsync(warning, cancellationToken);
        return CleanupResult(CustomLoopReceiptCleanupStatus.CommittedWithAuditWarning, warning, CustomLoopReceiptQuotaExhaustionReason.None, CustomLoopReceiptCleanupBlockReason.AuditUnavailable, warning.Detail);
    }

    private static CustomLoopReceiptCleanupResult ResultForJournal(CustomLoopReceiptCleanupJournal journal, bool replay)
    {
        return journal.Stage switch
        {
            CustomLoopReceiptCleanupStage.Completed when journal.Candidates.Length == 0 => CleanupResult(CustomLoopReceiptCleanupStatus.NothingEligible, journal, CustomLoopReceiptQuotaExhaustionReason.None, CustomLoopReceiptCleanupBlockReason.None, journal.Detail),
            CustomLoopReceiptCleanupStage.Completed => CleanupResult(replay ? CustomLoopReceiptCleanupStatus.Replayed : CustomLoopReceiptCleanupStatus.Pruned, journal, CustomLoopReceiptQuotaExhaustionReason.None, CustomLoopReceiptCleanupBlockReason.None, journal.Detail),
            CustomLoopReceiptCleanupStage.CommittedWithAuditWarning => CleanupResult(CustomLoopReceiptCleanupStatus.CommittedWithAuditWarning, journal, CustomLoopReceiptQuotaExhaustionReason.None, CustomLoopReceiptCleanupBlockReason.AuditUnavailable, journal.Detail),
            CustomLoopReceiptCleanupStage.AbandonedConflict => CleanupResult(CustomLoopReceiptCleanupStatus.CleanupConflict, journal, CustomLoopReceiptQuotaExhaustionReason.None, CustomLoopReceiptCleanupBlockReason.CleanupConflict, journal.Detail),
            CustomLoopReceiptCleanupStage.Degraded when journal.Outcome == CustomLoopReceiptCleanupOutcome.AuditUnavailable => CleanupResult(CustomLoopReceiptCleanupStatus.AuditUnavailable, journal, CustomLoopReceiptQuotaExhaustionReason.None, CustomLoopReceiptCleanupBlockReason.AuditUnavailable, journal.Detail),
            CustomLoopReceiptCleanupStage.Degraded when journal.Outcome == CustomLoopReceiptCleanupOutcome.Corrupt => CleanupResult(CustomLoopReceiptCleanupStatus.Corrupt, journal, CustomLoopReceiptQuotaExhaustionReason.None, CustomLoopReceiptCleanupBlockReason.CorruptEvidence, journal.Detail),
            CustomLoopReceiptCleanupStage.Degraded => CleanupResult(CustomLoopReceiptCleanupStatus.Degraded, journal, CustomLoopReceiptQuotaExhaustionReason.None, CustomLoopReceiptCleanupBlockReason.AmbiguousEvidence, journal.Detail),
            _ => CleanupResult(CustomLoopReceiptCleanupStatus.OperationInProgress, journal, CustomLoopReceiptQuotaExhaustionReason.None, CustomLoopReceiptCleanupBlockReason.OwnershipUnresolved, journal.Detail)
        };
    }

    private static CustomLoopReceiptCleanupResult CleanupResult(CustomLoopReceiptCleanupStatus status, CustomLoopReceiptCleanupJournal? journal, CustomLoopReceiptQuotaExhaustionReason exhaustionReason, CustomLoopReceiptCleanupBlockReason blockReason, string detail)
    {
        return new CustomLoopReceiptCleanupResult(status, journal, exhaustionReason, blockReason, journal?.RemovedArtifactCount ?? 0, journal?.RemovedArtifactUtf8Bytes ?? 0, detail);
    }

    private async Task<IReadOnlyList<(CustomLoopControlOperation Operation, byte[] Bytes, string Path)>> ReadAllOperationArtifactsAsync(CancellationToken cancellationToken, string? allowedOrphanOwnerOperationId = null)
    {
        if (!_pathGuard.DirectoryExists(_root))
        {
            return [];
        }

        ValidateOperationStorageForRead(allowedOrphanOwnerOperationId);

        var artifacts = new List<(CustomLoopControlOperation Operation, byte[] Bytes, string Path)>();
        foreach (var path in Directory.EnumerateFiles(_root, "*.json", SearchOption.TopDirectoryOnly).OrderBy(item => item, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operationId = Path.GetFileNameWithoutExtension(path);
            if (!CustomLoopArtifactIdentifier.IsValid(operationId, CustomLoopLimits.MaxMutationOperationIdCharacters))
            {
                throw new FormatException($"Lifecycle-control receipt filename `{Path.GetFileName(path)}` is not a canonical operation identity.");
            }

            var bytes = await _pathGuard.ReadAllBytesAsync(_root, path, MaximumArtifactBytes, "Custom-loop control operation", cancellationToken);
            CustomLoopJsonDepthPolicy.ValidatePersistedJsonDepth(bytes, _jsonOptions.MaxDepth, "Custom-loop control operation", path);
            CustomLoopControlOperation? operation;
            try
            {
                operation = JsonSerializer.Deserialize<CustomLoopControlOperation>(bytes, _jsonOptions);
            }
            catch (JsonException exception)
            {
                throw new FormatException($"Custom-loop control operation `{path}` is invalid JSON.", exception);
            }

            Validate(operation, requirePending: operation?.State == CustomLoopControlOperationState.Pending);
            if (operation!.OwnerGenerationId is null || operation.OwnerProcessId is null || operation.OwnerAcquiredAtUtc is null || !string.Equals(operation.OperationId, operationId, StringComparison.Ordinal))
            {
                throw new FormatException($"Lifecycle-control receipt `{path}` is missing ownership metadata or does not match its operation identity.");
            }

            artifacts.Add((operation, bytes, path));
        }

        return artifacts;
    }

    private async Task<(int Count, long Utf8Bytes)> ReadRawUsageAsync(CancellationToken cancellationToken, string? allowedOrphanOwnerOperationId = null)
    {
        var artifacts = await ReadAllOperationArtifactsAsync(cancellationToken, allowedOrphanOwnerOperationId);
        return (artifacts.Count, artifacts.Sum(item => (long)item.Bytes.Length));
    }

    private async Task<CustomLoopReceiptProofLedger?> ReadProofLedgerAsync(CancellationToken cancellationToken)
    {
        var path = _pathGuard.GetFilePath(_retentionRoot, "proof-ledger.json");
        if (!_pathGuard.DirectoryExists(_retentionRoot))
        {
            return null;
        }

        ValidateProofStorageForRead();
        if (!File.Exists(path))
        {
            return null;
        }

        var bytes = await _pathGuard.ReadAllBytesAsync(_retentionRoot, path, CustomLoopReceiptRetentionPolicy.MaxProofLedgerUtf8Bytes, "Custom-loop receipt proof ledger", cancellationToken);
        return CustomLoopReceiptRetentionContractCodec.DeserializeProofLedger(bytes);
    }

    private async Task WriteProofLedgerAsync(CustomLoopReceiptProofLedger ledger, CancellationToken cancellationToken)
    {
        var bytes = CustomLoopReceiptRetentionContractCodec.SerializeProofLedger(ledger);
        var path = _pathGuard.GetFilePath(_retentionRoot, "proof-ledger.json");
        await _pathGuard.WriteTextAtomicallyAsync(_retentionRoot, path, Encoding.UTF8.GetString(bytes), cancellationToken);
    }

    private async Task<CustomLoopReceiptCleanupJournal?> ReadCleanupJournalAsync(CancellationToken cancellationToken)
    {
        var path = _pathGuard.GetFilePath(_cleanupRoot, ActiveCleanupJournalFileName);
        if (!_pathGuard.DirectoryExists(_cleanupRoot))
        {
            return null;
        }

        ValidateCleanupStorageForRead();
        if (!File.Exists(path))
        {
            return null;
        }

        var bytes = await _pathGuard.ReadAllBytesAsync(_cleanupRoot, path, CustomLoopReceiptRetentionPolicy.MaxCleanupJournalUtf8Bytes, "Lifecycle-control receipt cleanup journal", cancellationToken);
        var journal = CustomLoopReceiptRetentionContractCodec.DeserializeCleanupJournal(bytes);
        if (journal.Request.ArtifactClass != ArtifactClass)
        {
            throw new FormatException("Lifecycle-control receipt cleanup journal targets a different artifact class.");
        }

        return journal;
    }

    private async Task WriteCleanupJournalAsync(CustomLoopReceiptCleanupJournal journal, CancellationToken cancellationToken)
    {
        if (journal.Request.ArtifactClass != ArtifactClass)
        {
            throw new ArgumentException("Lifecycle-control receipt cleanup journal must target lifecycle-control receipts.", nameof(journal));
        }

        var bytes = CustomLoopReceiptRetentionContractCodec.SerializeCleanupJournal(journal);
        var path = _pathGuard.GetFilePath(_cleanupRoot, ActiveCleanupJournalFileName);
        await _pathGuard.WriteTextAtomicallyAsync(_cleanupRoot, path, Encoding.UTF8.GetString(bytes), cancellationToken);
    }

    private async Task<CustomLoopExpiredOperationProof?> FindExpiredProofAsync(string operationId, CancellationToken cancellationToken)
    {
        var ledger = await ReadProofLedgerAsync(cancellationToken);
        return ledger?.ExpiredOperations.SingleOrDefault(item => item.ArtifactClass == ArtifactClass && string.Equals(item.OperationId, operationId, StringComparison.Ordinal));
    }

    private void ReclaimAbandonedInternalArtifactsUnderOwnership()
    {
        ReclaimOperationArtifactsUnderOwnership();
        ReclaimAtomicWriteTempsUnderOwnership(_cleanupRoot, fileName => string.Equals(fileName, ActiveCleanupJournalFileName, StringComparison.Ordinal), rejectOtherFiles: true);
        ReclaimAtomicWriteTempsUnderOwnership(_retentionRoot, fileName => string.Equals(fileName, "proof-ledger.json", StringComparison.Ordinal), rejectOtherFiles: false);
    }

    private void ReclaimOperationArtifactsUnderOwnership()
    {
        if (!_pathGuard.DirectoryExists(_root))
        {
            return;
        }

        if (Directory.EnumerateDirectories(_root, "*", SearchOption.TopDirectoryOnly).Any())
        {
            throw new FormatException("Lifecycle-control receipt storage cannot contain subdirectories.");
        }

        var files = Directory.EnumerateFiles(_root, "*", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        foreach (var path in files)
        {
            var fileName = Path.GetFileName(path);
            if (IsAtomicWriteTemp(fileName, IsCanonicalOperationArtifactFileName))
            {
                _pathGuard.DeleteFile(_root, path);
                continue;
            }

            if (IsCanonicalOperationArtifactFileName(fileName))
            {
                continue;
            }

            if (!TryGetOwnerLockOperationId(fileName, out var operationId))
            {
                throw new FormatException($"Lifecycle-control receipt storage contains an unrecognized artifact `{fileName}`.");
            }

            var receiptPath = _pathGuard.GetFilePath(_root, operationId + ".json");
            if (!File.Exists(receiptPath) && !TryDeleteInactiveOperationOwnerLock(operationId))
            {
                throw new InvalidOperationException($"Lifecycle-control operation `{operationId}` retains active ownership without a durable receipt.");
            }
        }
    }

    private void ReclaimAtomicWriteTempsUnderOwnership(string root, Func<string, bool> validTarget, bool rejectOtherFiles)
    {
        if (!_pathGuard.DirectoryExists(root))
        {
            return;
        }

        if (rejectOtherFiles && Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly).Any())
        {
            throw new FormatException("Lifecycle-control cleanup storage cannot contain subdirectories.");
        }

        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.Ordinal))
        {
            var fileName = Path.GetFileName(path);
            if (IsAtomicWriteTemp(fileName, validTarget))
            {
                _pathGuard.DeleteFile(root, path);
                continue;
            }

            if (rejectOtherFiles && !validTarget(fileName))
            {
                throw new FormatException($"Lifecycle-control cleanup storage contains an unrecognized artifact `{fileName}`.");
            }

            if (!rejectOtherFiles && LooksTemporary(fileName) && !string.Equals(fileName, ".custom-loop-mutations.lock", StringComparison.Ordinal))
            {
                throw new FormatException($"Shared receipt-retention storage contains an unrecognized temporary artifact `{fileName}`.");
            }
        }
    }

    private void ValidateOperationStorageForRead(string? allowedOrphanOwnerOperationId)
    {
        if (Directory.EnumerateDirectories(_root, "*", SearchOption.TopDirectoryOnly).Any())
        {
            throw new FormatException("Lifecycle-control receipt storage cannot contain subdirectories.");
        }

        var receiptIds = Directory.EnumerateFiles(_root, "*.json", SearchOption.TopDirectoryOnly)
            .Where(path => IsCanonicalOperationArtifactFileName(Path.GetFileName(path)))
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(_root, "*", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(path);
            if (IsCanonicalOperationArtifactFileName(fileName))
            {
                continue;
            }

            if (TryGetOwnerLockOperationId(fileName, out var operationId)
                && (receiptIds.Contains(operationId) || string.Equals(operationId, allowedOrphanOwnerOperationId, StringComparison.Ordinal)))
            {
                continue;
            }

            throw new FormatException($"Lifecycle-control receipt storage contains an unrecognized or orphaned internal artifact `{fileName}`.");
        }
    }

    private void ValidateCleanupStorageForRead()
    {
        if (Directory.EnumerateDirectories(_cleanupRoot, "*", SearchOption.TopDirectoryOnly).Any())
        {
            throw new FormatException("Lifecycle-control cleanup storage cannot contain subdirectories.");
        }

        foreach (var path in Directory.EnumerateFiles(_cleanupRoot, "*", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(path);
            if (!string.Equals(fileName, ActiveCleanupJournalFileName, StringComparison.Ordinal))
            {
                throw new FormatException($"Lifecycle-control cleanup storage contains an unrecognized internal artifact `{fileName}`.");
            }
        }
    }

    private void ValidateProofStorageForRead()
    {
        foreach (var path in Directory.EnumerateFiles(_retentionRoot, "*", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(path);
            if (IsAtomicWriteTemp(fileName, target => string.Equals(target, "proof-ledger.json", StringComparison.Ordinal))
                || LooksTemporary(fileName) && !string.Equals(fileName, ".custom-loop-mutations.lock", StringComparison.Ordinal))
            {
                throw new FormatException($"Shared receipt-retention storage contains an unrecognized or abandoned internal artifact `{fileName}`.");
            }
        }
    }

    private bool CanAcquireInactiveOperationOwnerLock(string operationId)
    {
        var path = _pathGuard.GetFilePath(_root, $".{operationId}.owner.lock");
        if (!File.Exists(path))
        {
            return true;
        }

        FileStream? stream = null;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
            return CustomLoopCrossProcessFileLock.TryAcquire(stream);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            stream?.Dispose();
        }
    }

    private bool TryDeleteInactiveOperationOwnerLock(string operationId)
    {
        var path = _pathGuard.GetFilePath(_root, $".{operationId}.owner.lock");
        if (!File.Exists(path))
        {
            return true;
        }

        if (!CanAcquireInactiveOperationOwnerLock(operationId))
        {
            return false;
        }

        try
        {
            _pathGuard.DeleteFile(_root, path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsCanonicalOperationArtifactFileName(string fileName)
    {
        return string.Equals(Path.GetExtension(fileName), ".json", StringComparison.Ordinal)
            && CustomLoopArtifactIdentifier.IsValid(Path.GetFileNameWithoutExtension(fileName), CustomLoopLimits.MaxMutationOperationIdCharacters);
    }

    private static bool TryGetOwnerLockOperationId(string fileName, out string operationId)
    {
        const string Prefix = ".";
        const string Suffix = ".owner.lock";
        operationId = string.Empty;
        if (!fileName.StartsWith(Prefix, StringComparison.Ordinal) || !fileName.EndsWith(Suffix, StringComparison.Ordinal))
        {
            return false;
        }

        operationId = fileName[Prefix.Length..^Suffix.Length];
        return CustomLoopArtifactIdentifier.IsValid(operationId, CustomLoopLimits.MaxMutationOperationIdCharacters);
    }

    private static bool IsAtomicWriteTemp(string fileName, Func<string, bool> validTarget)
    {
        const string Suffix = ".tmp";
        const int GuidLength = 32;
        if (fileName.Length == 0 || fileName[0] != '.' || !fileName.EndsWith(Suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var guidStart = fileName.Length - Suffix.Length - GuidLength;
        if (guidStart <= 2 || fileName[guidStart - 1] != '.')
        {
            return false;
        }

        var target = fileName[1..(guidStart - 1)];
        var guid = fileName.Substring(guidStart, GuidLength);
        return validTarget(target) && Guid.TryParseExact(guid, "N", out _);
    }

    private static bool LooksTemporary(string fileName) => fileName.StartsWith(".", StringComparison.Ordinal) || fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

    private static void ThrowIfRawAndCompactProofConflict(string operationId, CustomLoopControlOperation? exact, CustomLoopExpiredOperationProof? expired)
    {
        if (exact is not null && expired is not null)
        {
            throw new FormatException($"Lifecycle-control operation `{operationId}` has contradictory raw and compact expiry evidence.");
        }
    }

    private static List<CustomLoopReceiptCleanupCandidate>? SelectCandidates(IReadOnlyList<(CustomLoopControlOperation Operation, byte[] Bytes, string Path)> artifacts, CustomLoopReceiptProofLedger? ledger, CustomLoopReceiptCleanupRequest request, DateTimeOffset now)
    {
        var expiredProofIds = ledger?.ExpiredOperations.Where(item => item.ArtifactClass == CustomLoopReceiptArtifactClass.LifecycleControlReceipt).Select(item => item.OperationId).ToHashSet(StringComparer.Ordinal) ?? [];
        if (artifacts.Any(item => expiredProofIds.Contains(item.Operation.OperationId)))
        {
            return null;
        }

        var candidates = new List<CustomLoopReceiptCleanupCandidate>();
        var selectedBytes = 0L;
        foreach (var artifact in artifacts
            .Where(item => Classify(item.Operation, now) == CustomLoopReceiptArtifactCategory.Compactable)
            .OrderBy(item => item.Operation.UpdatedAtUtc)
            .ThenBy(item => item.Operation.OperationId, StringComparer.Ordinal))
        {
            if (candidates.Count == request.MaximumArtifactCount || selectedBytes + artifact.Bytes.Length > request.MaximumArtifactUtf8Bytes)
            {
                break;
            }

            var completedAtUtc = artifact.Operation.UpdatedAtUtc;
            if (completedAtUtc > request.ReplayCutoffUtc)
            {
                continue;
            }

            var hash = Hash(artifact.Bytes);
            var proof = new CustomLoopExpiredOperationProof(CustomLoopExpiredOperationProof.CurrentSchemaVersion, CustomLoopReceiptArtifactClass.LifecycleControlReceipt, null, null, null, artifact.Operation.OperationId, artifact.Operation.RequestHash, hash, completedAtUtc, completedAtUtc + CustomLoopReceiptRetentionPolicy.ExactReplayDuration);
            candidates.Add(new CustomLoopReceiptCleanupCandidate(artifact.Operation.OperationId, hash, artifact.Bytes.Length, CustomLoopReceiptArtifactCategory.Compactable, true, true, proof, null));
            selectedBytes += artifact.Bytes.Length;
        }

        return candidates;
    }

    private static (CustomLoopReceiptQuotaExhaustionReason ExhaustionReason, int ExistingCount, long ExistingBytes, int AddedCount, long AddedBytes) MeasureProofUsage(CustomLoopReceiptProofLedger? ledger, IEnumerable<CustomLoopReceiptCleanupCandidate> candidates)
    {
        var existing = ledger?.ExpiredOperations.Where(item => item.ArtifactClass == CustomLoopReceiptArtifactClass.LifecycleControlReceipt).OrderBy(item => item.OperationId, StringComparer.Ordinal).ToArray() ?? [];
        var existingBytes = MeasureProofBytes(existing);
        var additions = candidates.Select(item => item.ExpiredOperationProof!).OrderBy(item => item.OperationId, StringComparer.Ordinal).ToArray();
        if (additions.Select(item => item.OperationId).Distinct(StringComparer.Ordinal).Count() != additions.Length || existing.Any(item => additions.Any(addition => string.Equals(addition.OperationId, item.OperationId, StringComparison.Ordinal))))
        {
            return (CustomLoopReceiptQuotaExhaustionReason.ProofCountLimit, existing.Length, existingBytes, additions.Length, MeasureProofBytes(additions));
        }

        var addedBytes = MeasureProofBytes(additions) + (existing.Length > 0 && additions.Length > 0 ? 1 : 0);
        var budget = CustomLoopReceiptRetentionPolicy.GetBudget(CustomLoopReceiptArtifactClass.LifecycleControlReceipt);
        if (existing.Length + additions.Length > budget.MaximumProofCount)
        {
            return (CustomLoopReceiptQuotaExhaustionReason.ProofCountLimit, existing.Length, existingBytes, additions.Length, addedBytes);
        }

        if (existingBytes + addedBytes > budget.MaximumProofUtf8Bytes)
        {
            return (CustomLoopReceiptQuotaExhaustionReason.ProofByteLimit, existing.Length, existingBytes, additions.Length, addedBytes);
        }

        return (CustomLoopReceiptQuotaExhaustionReason.None, existing.Length, existingBytes, additions.Length, addedBytes);
    }

    private static (bool AllEquivalent, ImmutableArray<CustomLoopReceiptCleanupCandidate> MissingCandidates) ResolveProofAdditions(CustomLoopReceiptProofLedger? ledger, ImmutableArray<CustomLoopReceiptCleanupCandidate> candidates)
    {
        var existing = ledger?.ExpiredOperations
            .Where(item => item.ArtifactClass == CustomLoopReceiptArtifactClass.LifecycleControlReceipt)
            .ToDictionary(item => item.OperationId, StringComparer.Ordinal) ?? [];
        var missing = ImmutableArray.CreateBuilder<CustomLoopReceiptCleanupCandidate>();
        foreach (var candidate in candidates.OrderBy(item => item.ArtifactId, StringComparer.Ordinal))
        {
            var expected = candidate.ExpiredOperationProof!;
            if (!existing.TryGetValue(expected.OperationId, out var persisted))
            {
                missing.Add(candidate);
                continue;
            }

            if (persisted != expected)
            {
                return (false, ImmutableArray<CustomLoopReceiptCleanupCandidate>.Empty);
            }
        }

        return (true, missing.ToImmutable());
    }

    private static long MeasureProofBytes(IEnumerable<CustomLoopExpiredOperationProof> proofs)
    {
        var count = 0;
        long bytes = 0;
        foreach (var proof in proofs)
        {
            bytes += CustomLoopReceiptRetentionContractCodec.MeasureExpiredOperationProofUtf8Bytes(proof) + (count == 0 ? 0 : 1);
            count++;
        }

        return bytes;
    }

    private static CustomLoopReceiptProofLedger AppendProofs(CustomLoopReceiptProofLedger? existing, ImmutableArray<CustomLoopReceiptCleanupCandidate> candidates, DateTimeOffset createdAtUtc)
    {
        var existingOperations = existing?.ExpiredOperations ?? ImmutableArray<CustomLoopExpiredOperationProof>.Empty;
        var operations = existingOperations.AddRange(candidates.Select(item => item.ExpiredOperationProof!));
        var generation = existing is null ? 1 : checked(existing.Generation + 1);
        var previousHash = existing is null ? null : CustomLoopReceiptRetentionContractCodec.ComputeProofLedgerHash(existing);
        return new CustomLoopReceiptProofLedger(CustomLoopReceiptProofLedger.CurrentSchemaVersion, generation, createdAtUtc, previousHash, existing?.DefinitionLineage ?? ImmutableArray<CustomLoopDefinitionLineageProof>.Empty, operations);
    }

    private async Task<(bool AllMatch, IReadOnlyList<(CustomLoopControlOperation Operation, byte[] Bytes, string Path)> Artifacts)> RevalidateCandidatesAsync(CustomLoopReceiptCleanupJournal journal, CancellationToken cancellationToken)
    {
        var artifacts = await ReadAllOperationArtifactsAsync(cancellationToken);
        if (artifacts.Count != 0)
        {
            var byId = artifacts.ToDictionary(item => item.Operation.OperationId, StringComparer.Ordinal);
            var now = TrustedUtcNow();
            var allMatch = journal.Candidates.All(candidate => byId.TryGetValue(candidate.ArtifactId, out var artifact)
                && string.Equals(Hash(artifact.Bytes), candidate.ArtifactHash, StringComparison.Ordinal)
                && artifact.Bytes.Length == candidate.ArtifactUtf8Bytes
                && Classify(artifact.Operation, now) == CustomLoopReceiptArtifactCategory.Compactable
                && CanAcquireInactiveOperationOwnerLock(candidate.ArtifactId));
            return (allMatch, artifacts);
        }

        return (false, artifacts);
    }

    private async Task<(bool IsCanonical, int AttributedRemovedCount, long AttributedRemovedBytes)> ReconcileRemovalProgressAsync(CustomLoopReceiptCleanupJournal journal, CancellationToken cancellationToken)
    {
        var artifacts = await ReadAllOperationArtifactsAsync(cancellationToken);
        var byId = artifacts.ToDictionary(item => item.Operation.OperationId, StringComparer.Ordinal);
        var candidates = journal.Candidates.OrderBy(item => item.ArtifactId, StringComparer.Ordinal).ToArray();
        var now = TrustedUtcNow();
        var missingPrefixCount = 0;
        var retainedCandidateSeen = false;
        var canonical = true;
        foreach (var candidate in candidates)
        {
            if (!byId.TryGetValue(candidate.ArtifactId, out var artifact))
            {
                if (retainedCandidateSeen)
                {
                    canonical = false;
                    break;
                }

                missingPrefixCount++;
                continue;
            }

            retainedCandidateSeen = true;
            if (!string.Equals(Hash(artifact.Bytes), candidate.ArtifactHash, StringComparison.Ordinal)
                || artifact.Bytes.Length != candidate.ArtifactUtf8Bytes
                || Classify(artifact.Operation, now) != CustomLoopReceiptArtifactCategory.Compactable)
            {
                canonical = false;
                break;
            }
        }

        var attributedCount = Math.Max(journal.RemovedArtifactCount, missingPrefixCount);
        var attributedBytes = candidates.Take(attributedCount).Sum(item => item.ArtifactUtf8Bytes);
        if (journal.RemovedArtifactCount > missingPrefixCount)
        {
            canonical = false;
        }

        return (canonical, attributedCount, attributedBytes);
    }

    private static CustomLoopReceiptCleanupJournal CreateJournal(CustomLoopReceiptCleanupRequest request, ImmutableArray<CustomLoopReceiptCleanupCandidate> candidates, CustomLoopReceiptCleanupStage stage, CustomLoopReceiptCleanupOutcome outcome, string? proofLedgerHash, int removedCount, long removedBytes, string detail, DateTimeOffset trustedNow)
    {
        var journal = new CustomLoopReceiptCleanupJournal(CustomLoopReceiptCleanupJournal.CurrentSchemaVersion, request, CustomLoopReceiptRetentionContractCodec.ComputeCleanupRequestHash(request), "cleanup-owner-" + Guid.NewGuid().ToString("N"), Environment.ProcessId, trustedNow, stage, outcome, trustedNow, candidates, proofLedgerHash, removedCount, removedBytes, detail);
        CustomLoopReceiptRetentionContractValidator.ValidateCleanupJournal(journal);
        return journal;
    }

    private CustomLoopReceiptCleanupJournal Reown(CustomLoopReceiptCleanupJournal journal)
    {
        var now = TrustedUtcNow();
        var reowned = journal with
        {
            OwnerGenerationId = "cleanup-owner-" + Guid.NewGuid().ToString("N"),
            OwnerProcessId = Environment.ProcessId,
            OwnershipAcquiredAtUtc = now,
            UpdatedAtUtc = now,
            Detail = "A bounded lifecycle-control receipt cleanup owner recovered the durable journal after its prior ownership window elapsed."
        };
        CustomLoopReceiptRetentionContractValidator.ValidateCleanupJournal(reowned);
        return reowned;
    }

    private CustomLoopReceiptCleanupJournal Advance(CustomLoopReceiptCleanupJournal journal, CustomLoopReceiptCleanupStage stage, CustomLoopReceiptCleanupOutcome outcome, string? proofLedgerHash, int removedCount, long removedBytes, string detail)
    {
        var advanced = journal with
        {
            Stage = stage,
            Outcome = outcome,
            UpdatedAtUtc = MonotonicTrustedUtcNow(journal.UpdatedAtUtc),
            ProofLedgerHash = proofLedgerHash,
            RemovedArtifactCount = removedCount,
            RemovedArtifactUtf8Bytes = removedBytes,
            Detail = detail
        };
        CustomLoopReceiptRetentionContractValidator.ValidateCleanupJournal(advanced);
        return advanced;
    }

    private static AuditEvent CreateRetentionAudit(CustomLoopReceiptCleanupJournal journal, string action, string outcome, string detail)
    {
        return AuditEvent.Create(journal.Request.Actor, action, journal.Request.OperationId, outcome, detail, new Dictionary<string, object?>
        {
            ["artifactClass"] = journal.Request.ArtifactClass.ToString(),
            ["requestHash"] = journal.RequestHash,
            ["candidateCount"] = journal.Candidates.Length,
            ["candidateUtf8Bytes"] = journal.Candidates.Sum(item => item.ArtifactUtf8Bytes),
            ["ownerGenerationId"] = journal.OwnerGenerationId
        });
    }

    private static Dictionary<CustomLoopReceiptArtifactCategory, CustomLoopReceiptCategoryUsage> CreateEmptyUsage()
    {
        return Enum.GetValues<CustomLoopReceiptArtifactCategory>()
            .Where(item => item != CustomLoopReceiptArtifactCategory.Unknown)
            .ToDictionary(item => item, item => new CustomLoopReceiptCategoryUsage(item, 0, 0));
    }

    private static void AddUsage(Dictionary<CustomLoopReceiptArtifactCategory, CustomLoopReceiptCategoryUsage> categories, CustomLoopReceiptArtifactCategory category, long utf8Bytes)
    {
        var current = categories[category];
        categories[category] = current with { ArtifactCount = checked(current.ArtifactCount + 1), Utf8Bytes = checked(current.Utf8Bytes + Math.Max(1, utf8Bytes)) };
    }

    private static (int ArtifactCount, long ArtifactUtf8Bytes, int ProofCount, long ProofUtf8Bytes) SummarizeUsage(Dictionary<CustomLoopReceiptArtifactCategory, CustomLoopReceiptCategoryUsage> categories)
    {
        var raw = categories.Where(item => item.Key is not (CustomLoopReceiptArtifactCategory.RetainedLineage or CustomLoopReceiptArtifactCategory.ExpiredIdempotency)).Select(item => item.Value).ToArray();
        var proof = categories.Where(item => item.Key is CustomLoopReceiptArtifactCategory.RetainedLineage or CustomLoopReceiptArtifactCategory.ExpiredIdempotency).Select(item => item.Value).ToArray();
        return (raw.Sum(item => item.ArtifactCount), raw.Sum(item => item.Utf8Bytes), proof.Sum(item => item.ArtifactCount), proof.Sum(item => item.Utf8Bytes));
    }

    private static CustomLoopReceiptQuotaExhaustionReason DetermineExhaustionReason(int rawCount, long rawBytes, int proofCount, long proofBytes)
    {
        var budget = CustomLoopReceiptRetentionPolicy.GetBudget(CustomLoopReceiptArtifactClass.LifecycleControlReceipt);
        if (rawCount >= budget.MaximumArtifactCount)
        {
            return CustomLoopReceiptQuotaExhaustionReason.ArtifactCountLimit;
        }

        if (rawBytes >= budget.MaximumArtifactUtf8Bytes)
        {
            return CustomLoopReceiptQuotaExhaustionReason.ArtifactByteLimit;
        }

        if (rawCount >= budget.NormalAdmissionArtifactCount)
        {
            return CustomLoopReceiptQuotaExhaustionReason.ReservedArtifactCountLimit;
        }

        if (rawBytes >= budget.NormalAdmissionArtifactUtf8Bytes)
        {
            return CustomLoopReceiptQuotaExhaustionReason.ReservedArtifactByteLimit;
        }

        if (proofCount >= budget.MaximumProofCount)
        {
            return CustomLoopReceiptQuotaExhaustionReason.ProofCountLimit;
        }

        return proofBytes >= budget.MaximumProofUtf8Bytes ? CustomLoopReceiptQuotaExhaustionReason.ProofByteLimit : CustomLoopReceiptQuotaExhaustionReason.None;
    }

    private static CustomLoopReceiptCleanupBlockReason DetermineBlockReason(Dictionary<CustomLoopReceiptArtifactCategory, CustomLoopReceiptCategoryUsage> categories, CustomLoopReceiptQuotaExhaustionReason exhaustionReason)
    {
        if (categories[CustomLoopReceiptArtifactCategory.Corrupt].ArtifactCount > 0)
        {
            return CustomLoopReceiptCleanupBlockReason.CorruptEvidence;
        }

        if (categories[CustomLoopReceiptArtifactCategory.Ambiguous].ArtifactCount > 0)
        {
            return CustomLoopReceiptCleanupBlockReason.AmbiguousEvidence;
        }

        if (categories[CustomLoopReceiptArtifactCategory.OwnershipUnresolved].ArtifactCount > 0)
        {
            return CustomLoopReceiptCleanupBlockReason.OwnershipUnresolved;
        }

        if (exhaustionReason is CustomLoopReceiptQuotaExhaustionReason.ProofCountLimit or CustomLoopReceiptQuotaExhaustionReason.ProofByteLimit)
        {
            return CustomLoopReceiptCleanupBlockReason.ProofCapacityExhausted;
        }

        if (categories[CustomLoopReceiptArtifactCategory.Compactable].ArtifactCount == 0
            && categories[CustomLoopReceiptArtifactCategory.Pending].ArtifactCount > 0)
        {
            return CustomLoopReceiptCleanupBlockReason.PendingEvidence;
        }

        if (categories[CustomLoopReceiptArtifactCategory.Compactable].ArtifactCount == 0
            && categories[CustomLoopReceiptArtifactCategory.Unaudited].ArtifactCount > 0)
        {
            return CustomLoopReceiptCleanupBlockReason.UnauditedEvidence;
        }

        return CustomLoopReceiptCleanupBlockReason.None;
    }

    private static CustomLoopReceiptCleanupBlockReason DetermineNoCandidateBlockReason(IReadOnlyList<(CustomLoopControlOperation Operation, byte[] Bytes, string Path)> artifacts, DateTimeOffset now)
    {
        if (artifacts.Any(item => item.Operation.State == CustomLoopControlOperationState.Pending))
        {
            return CustomLoopReceiptCleanupBlockReason.PendingEvidence;
        }

        if (artifacts.Any(item => item.Operation.State == CustomLoopControlOperationState.Complete && !item.Operation.OutcomeAuditRecorded))
        {
            return CustomLoopReceiptCleanupBlockReason.UnauditedEvidence;
        }

        return CustomLoopReceiptCleanupBlockReason.None;
    }

    private static CustomLoopReceiptArtifactCategory Classify(CustomLoopControlOperation operation, DateTimeOffset now)
    {
        if (operation.State == CustomLoopControlOperationState.Pending)
        {
            return CustomLoopReceiptArtifactCategory.Pending;
        }

        if (!operation.OutcomeAuditRecorded)
        {
            return CustomLoopReceiptArtifactCategory.Unaudited;
        }

        return CustomLoopReceiptRetentionPolicy.IsExactReplayExpired(operation.UpdatedAtUtc, now)
            ? CustomLoopReceiptArtifactCategory.Compactable
            : CustomLoopReceiptArtifactCategory.Live;
    }

    private static bool IsCleanupActive(CustomLoopReceiptCleanupStage stage)
    {
        return stage is CustomLoopReceiptCleanupStage.IntentPersisted
            or CustomLoopReceiptCleanupStage.IntentAuditStarted
            or CustomLoopReceiptCleanupStage.IntentAuditRecorded
            or CustomLoopReceiptCleanupStage.ProofLedgerWritten
            or CustomLoopReceiptCleanupStage.ArtifactsRemoved
            or CustomLoopReceiptCleanupStage.OutcomeAuditStarted;
    }

    private static bool IsCleanupTerminal(CustomLoopReceiptCleanupStage stage)
    {
        return stage is CustomLoopReceiptCleanupStage.Completed
            or CustomLoopReceiptCleanupStage.CommittedWithAuditWarning
            or CustomLoopReceiptCleanupStage.AbandonedConflict
            or CustomLoopReceiptCleanupStage.Degraded;
    }

    private static bool IsInsideCleanupOwnershipWindow(CustomLoopReceiptCleanupJournal journal, DateTimeOffset now)
    {
        return now < journal.OwnershipAcquiredAtUtc || now - journal.OwnershipAcquiredAtUtc < CustomLoopReceiptRetentionPolicy.CleanupOwnershipWindow;
    }

    private static bool IsFutureCleanupRequest(CustomLoopReceiptCleanupRequest request, DateTimeOffset trustedNow)
    {
        return request.RequestedAtUtc > trustedNow || request.ReplayCutoffUtc > CustomLoopReceiptRetentionPolicy.GetReplayCutoffUtc(trustedNow);
    }

    private DateTimeOffset TrustedUtcNow() => _timeProvider.GetUtcNow().ToUniversalTime();

    private DateTimeOffset MonotonicTrustedUtcNow(DateTimeOffset minimum)
    {
        var now = TrustedUtcNow();
        return now < minimum ? minimum : now;
    }

    private static string Hash(ReadOnlySpan<byte> content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private ControlOperationLease? TryAcquireOperationOwnership(string operationId)
    {
        var path = _pathGuard.GetFilePath(_root, $".{operationId}.owner.lock");
        FileStream? ownership = null;
        try
        {
            ownership = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 1, FileOptions.WriteThrough);
            _pathGuard.GetFilePath(_root, $".{operationId}.owner.lock");
            if (!CustomLoopCrossProcessFileLock.TryAcquire(ownership))
            {
                ownership.Dispose();
                return null;
            }

            return new ControlOperationLease(operationId, "control-owner-" + Guid.NewGuid().ToString("N"), ownership);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ownership?.Dispose();
            return null;
        }
        catch
        {
            ownership?.Dispose();
            throw;
        }
    }

    private CustomLoopControlOperation WithOwnership(CustomLoopControlOperation operation, ControlOperationLease lease, string detail)
    {
        var acquiredAtUtc = MonotonicTrustedUtcNow(operation.CreatedAtUtc);

        var updatedAtUtc = acquiredAtUtc > operation.UpdatedAtUtc ? acquiredAtUtc : operation.UpdatedAtUtc;
        return operation with
        {
            OwnerGenerationId = lease.OwnerGenerationId,
            OwnerProcessId = Environment.ProcessId,
            OwnerAcquiredAtUtc = acquiredAtUtc,
            UpdatedAtUtc = updatedAtUtc,
            Detail = detail
        };
    }

    private static bool SameRequest(CustomLoopControlOperation left, CustomLoopControlOperation right)
    {
        return string.Equals(left.RequestHash, right.RequestHash, StringComparison.Ordinal)
            && left.Kind == right.Kind
            && string.Equals(left.RunId, right.RunId, StringComparison.Ordinal)
            && left.ExpectedLifecycleVersion == right.ExpectedLifecycleVersion
            && string.Equals(left.Actor, right.Actor, StringComparison.Ordinal);
    }

    private static void Validate(CustomLoopControlOperation? operation, bool requirePending)
    {
        if (operation is null)
        {
            throw new FormatException("Custom-loop control operation cannot be null.");
        }

        if (operation.SchemaVersion != CustomLoopControlOperation.CurrentSchemaVersion
            || !CustomLoopArtifactIdentifier.IsValid(operation.OperationId, CustomLoopLimits.MaxMutationOperationIdCharacters)
            || !CustomLoopArtifactIdentifier.IsValid(operation.RunId)
            || operation.ExpectedLifecycleVersion < 1
            || !Enum.IsDefined(operation.Kind)
            || operation.Kind == CustomLoopControlKind.Unknown
            || !Enum.IsDefined(operation.State)
            || operation.State == CustomLoopControlOperationState.Unknown
            || !Enum.IsDefined(operation.Outcome)
            || string.IsNullOrWhiteSpace(operation.Actor)
            || operation.Actor.Length > CustomLoopLimits.MaxTraceReferenceCharacters
            || !operation.Actor.IsNormalized(NormalizationForm.FormC)
            || operation.Actor.Any(character => char.IsControl(character) || char.IsSurrogate(character))
            || operation.RequestHash is not { Length: CustomLoopLimits.Sha256HexCharacters }
            || !operation.RequestHash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
            || !CustomLoopControlRequestHash.Matches(operation)
            || operation.CreatedAtUtc == default
            || operation.CreatedAtUtc.Offset != TimeSpan.Zero
            || operation.UpdatedAtUtc == default
            || operation.UpdatedAtUtc.Offset != TimeSpan.Zero
            || operation.UpdatedAtUtc < operation.CreatedAtUtc
            || string.IsNullOrWhiteSpace(operation.Detail)
            || operation.Detail.Length > CustomLoopLimits.MaxRunDetailCharacters)
        {
            throw new FormatException("Custom-loop control operation failed canonical validation.");
        }

        if (requirePending && (operation.State != CustomLoopControlOperationState.Pending || operation.Outcome != CustomLoopControlStatus.Unknown || operation.ResultLifecycleVersion is not null || operation.ResultRunStatus is not null || operation.OutcomeAuditRecorded))
        {
            throw new FormatException("Pending custom-loop control operation contains completed outcome fields.");
        }

        var hasLifecycleVersion = operation.ResultLifecycleVersion is not null;
        var hasRunStatus = operation.ResultRunStatus is not null;
        var allowsMissingRun = operation.Outcome is CustomLoopControlStatus.NotFound or CustomLoopControlStatus.Failed;
        if (operation.State == CustomLoopControlOperationState.Complete && (operation.Outcome == CustomLoopControlStatus.Unknown || hasLifecycleVersion != hasRunStatus || !hasLifecycleVersion && !allowsMissingRun))
        {
            throw new FormatException("Completed custom-loop control operation is missing its durable outcome.");
        }

        var hasAnyOwner = operation.OwnerGenerationId is not null || operation.OwnerProcessId is not null || operation.OwnerAcquiredAtUtc is not null;
        var hasCompleteOwner = operation.OwnerGenerationId is not null && operation.OwnerProcessId is not null && operation.OwnerAcquiredAtUtc is not null;
        if (hasAnyOwner && (!hasCompleteOwner
            || !CustomLoopArtifactIdentifier.IsValid(operation.OwnerGenerationId!)
            || operation.OwnerProcessId <= 0
            || operation.OwnerAcquiredAtUtc!.Value.Offset != TimeSpan.Zero
            || operation.OwnerAcquiredAtUtc.Value < operation.CreatedAtUtc
            || operation.OwnerAcquiredAtUtc.Value > operation.UpdatedAtUtc))
        {
            throw new FormatException("Custom-loop control operation ownership metadata is invalid.");
        }
    }

}
