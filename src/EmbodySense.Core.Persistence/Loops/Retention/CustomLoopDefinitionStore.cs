using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Application.Loops.ReceiptRetention;
using EmbodySense.Core.Application.Loops.ReceiptRetention.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Retention;
using EmbodySense.Core.Common.Loops.Models.Custom.Retention;
using EmbodySense.Core.Persistence.Loops.Models;

namespace EmbodySense.Core.Persistence.Loops;

public sealed partial class CustomLoopDefinitionStore
{
    private async Task<CustomLoopReceiptQuotaExhaustionReason> GetNewOperationAdmissionExhaustionAsync(CustomLoopDefinitionMutationRequest mutation, CancellationToken cancellationToken)
    {
        var operations = await ReadRetentionArtifactsAsync(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, cancellationToken);
        var operationBudget = CustomLoopReceiptRetentionPolicy.GetBudget(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt);
        var operationArtifactExhaustion = operationBudget.GetArtifactExhaustionReason(operations.Count, operations.Sum(item => item.Utf8Json.LongLength), 1, MaxDefinitionMutationOperationArtifactBytes, integrityPreservingCompletion: false);
        if (operationArtifactExhaustion != CustomLoopReceiptQuotaExhaustionReason.None)
        {
            return operationArtifactExhaustion;
        }

        var ledger = await ReadProofLedgerAsync(cancellationToken);
        var retainedOperationProofs = ledger?.ExpiredOperations.Where(item => item.ArtifactClass == CustomLoopReceiptArtifactClass.DefinitionMutationReceipt).ToArray() ?? [];
        var retainedOperationProofsById = retainedOperationProofs.ToDictionary(item => item.OperationId, StringComparer.Ordinal);
        var outstandingOperationProofs = new List<CustomLoopExpiredOperationProof>();
        foreach (var artifact in operations)
        {
            if (retainedOperationProofsById.TryGetValue(artifact.ArtifactId, out var retainedProof))
            {
                var retainedLineage = ledger?.DefinitionLineage.SingleOrDefault(item => string.Equals(item.LastMutationOperationId, artifact.ArtifactId, StringComparison.Ordinal));
                if (!ProofMatchesOperation(retainedProof, artifact, retainedLineage))
                {
                    throw new FormatException($"Definition mutation receipt `{artifact.ArtifactId}` conflicts with its retained compact proof.");
                }

                continue;
            }

            outstandingOperationProofs.Add(ToOutstandingExpiredProof(artifact));
        }

        var retainedOperationUsage = GetProofUsage(retainedOperationProofs, CustomLoopReceiptRetentionContractCodec.MeasureExpiredOperationProofUtf8Bytes);
        var outstandingOperationUsage = GetProofUsage(outstandingOperationProofs, CustomLoopReceiptRetentionContractCodec.MeasureExpiredOperationProofUtf8Bytes);
        var prospectiveOperationProofBytes = CustomLoopReceiptRetentionContractCodec.MeasureExpiredOperationProofUtf8Bytes(ToAdmissionExpiredProof(mutation));
        var operationProofExhaustion = operationBudget.GetProofAdmissionExhaustionReason(retainedOperationUsage.Count, retainedOperationUsage.Utf8Bytes, outstandingOperationUsage.Count, outstandingOperationUsage.Utf8Bytes, 1, prospectiveOperationProofBytes);
        if (operationProofExhaustion != CustomLoopReceiptQuotaExhaustionReason.None)
        {
            return operationProofExhaustion;
        }

        if (mutation.Kind != CustomLoopDefinitionMutationKind.Delete || mutation.PriorDefinition is null)
        {
            return CustomLoopReceiptQuotaExhaustionReason.None;
        }

        var tombstones = await ReadRetentionArtifactsAsync(CustomLoopReceiptArtifactClass.DefinitionTombstone, cancellationToken);
        var tombstoneBudget = CustomLoopReceiptRetentionPolicy.GetBudget(CustomLoopReceiptArtifactClass.DefinitionTombstone);
        var tombstoneArtifactExhaustion = tombstoneBudget.GetArtifactExhaustionReason(tombstones.Count, tombstones.Sum(item => item.Utf8Json.LongLength), 1, MaxTombstoneArtifactBytes, integrityPreservingCompletion: false);
        if (tombstoneArtifactExhaustion != CustomLoopReceiptQuotaExhaustionReason.None)
        {
            return tombstoneArtifactExhaustion;
        }

        var retainedLineageProofs = ledger?.DefinitionLineage ?? [];
        var retainedLineageByLoopId = retainedLineageProofs.ToDictionary(item => item.LoopId, StringComparer.Ordinal);
        var operationsById = operations.ToDictionary(item => item.ArtifactId, StringComparer.Ordinal);
        var outstandingLineage = new List<CustomLoopDefinitionLineageProof>();
        foreach (var tombstoneArtifact in tombstones)
        {
            var tombstone = tombstoneArtifact.Tombstone!;
            if (retainedLineageByLoopId.TryGetValue(tombstone.LoopId, out var existingLineage))
            {
                if (!LineageMatchesTombstone(existingLineage, tombstone))
                {
                    throw new FormatException($"Definition tombstone `{tombstone.LoopId}` conflicts with retained lineage proof.");
                }

                continue;
            }

            if (!operationsById.TryGetValue(tombstone.MutationOperationId, out var deleteArtifact) || !DeleteOperationMatchesTombstone(deleteArtifact.Operation!, tombstone))
            {
                throw new FormatException($"Definition tombstone `{tombstone.LoopId}` has no raw or compact lineage proof obligation.");
            }

            outstandingLineage.Add(ToLineageProof(deleteArtifact.Operation!, tombstone));
        }

        var retainedLineageUsage = GetProofUsage(retainedLineageProofs, CustomLoopReceiptRetentionContractCodec.MeasureDefinitionLineageProofUtf8Bytes);
        var outstandingLineageUsage = GetProofUsage(outstandingLineage, CustomLoopReceiptRetentionContractCodec.MeasureDefinitionLineageProofUtf8Bytes);
        var prospectiveLineageProofBytes = CustomLoopReceiptRetentionContractCodec.MeasureDefinitionLineageProofUtf8Bytes(ToAdmissionLineageProof(mutation));
        return tombstoneBudget.GetProofAdmissionExhaustionReason(retainedLineageUsage.Count, retainedLineageUsage.Utf8Bytes, outstandingLineageUsage.Count, outstandingLineageUsage.Utf8Bytes, 1, prospectiveLineageProofBytes);
    }

    private async Task<bool> CanWriteOperationAsync(CustomLoopDefinitionMutationOperationRecord operation, CancellationToken cancellationToken, bool integrityPreservingCompletion)
    {
        var path = GetOperationPath(operation.OperationId);
        var bytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(operation, _jsonOptions) + Environment.NewLine);
        if (bytes > MaxDefinitionMutationOperationArtifactBytes)
        {
            return false;
        }

        var artifacts = await ReadRetentionArtifactsAsync(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, cancellationToken);
        var existing = artifacts.SingleOrDefault(item => string.Equals(item.ArtifactId, operation.OperationId, StringComparison.Ordinal));
        var count = artifacts.Count - (existing is null ? 0 : 1);
        var aggregateBytes = artifacts.Sum(item => item.Utf8Json.LongLength) - (existing?.Utf8Json.LongLength ?? 0);
        var completion = integrityPreservingCompletion || File.Exists(path);
        return CustomLoopReceiptRetentionPolicy.GetBudget(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt).CanAccountArtifacts(count, aggregateBytes, 1, bytes, completion);
    }

    private async Task<bool> CanWriteTombstoneAsync(CustomLoopDefinitionTombstone tombstone, bool integrityPreservingCompletion, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(tombstone, _jsonOptions) + Environment.NewLine);
        if (bytes > MaxTombstoneArtifactBytes)
        {
            return false;
        }

        var artifacts = await ReadRetentionArtifactsAsync(CustomLoopReceiptArtifactClass.DefinitionTombstone, cancellationToken);
        var existing = artifacts.SingleOrDefault(item => string.Equals(item.ArtifactId, tombstone.LoopId, StringComparison.Ordinal));
        var count = artifacts.Count - (existing is null ? 0 : 1);
        var aggregateBytes = artifacts.Sum(item => item.Utf8Json.LongLength) - (existing?.Utf8Json.LongLength ?? 0);
        return CustomLoopReceiptRetentionPolicy.GetBudget(CustomLoopReceiptArtifactClass.DefinitionTombstone).CanAccountArtifacts(count, aggregateBytes, 1, bytes, integrityPreservingCompletion || existing is not null);
    }

    private async Task WriteTombstoneAsync(CustomLoopDefinitionTombstone tombstone, bool integrityPreservingCompletion, CancellationToken cancellationToken)
    {
        if (!await CanWriteTombstoneAsync(tombstone, integrityPreservingCompletion, cancellationToken))
        {
            throw new IOException("Definition tombstone quota is exhausted; the write failed before replacing durable evidence.");
        }

        await WriteJsonAsync(_paths.CustomLoopDefinitionTombstonesPath, GetTombstonePath(tombstone.LoopId), tombstone, cancellationToken);
    }

    private static bool HasExpiredMutationProof(WorkspaceState state, string operationId)
    {
        return state.ProofLedger?.ExpiredOperations.Any(item => item.ArtifactClass == CustomLoopReceiptArtifactClass.DefinitionMutationReceipt && string.Equals(item.OperationId, operationId, StringComparison.Ordinal)) == true;
    }

    private static CustomLoopDefinitionTombstone? GetCompactedTombstone(WorkspaceState state, string loopId)
    {
        var lineage = state.ProofLedger?.DefinitionLineage.SingleOrDefault(item => string.Equals(item.LoopId, loopId, StringComparison.Ordinal));
        return lineage is null || !lineage.IsDeleted || lineage.DeletedAtUtc is null
            ? null
            : new CustomLoopDefinitionTombstone(
                CustomLoopDefinitionTombstone.CurrentSchemaVersion,
                lineage.LoopId,
                lineage.LastDefinitionVersion,
                lineage.LastDefinitionHash,
                lineage.LastMutationOperationId,
                lineage.DeletedAtUtc.Value);
    }

    /// <summary>
    /// Creates a class-specific #225 retention port backed by this store's shared authoring lock and lineage state.
    /// </summary>
    /// <param name="artifactClass">The definition-mutation receipt or definition-tombstone class.</param>
    /// <returns>The class-specific retention port.</returns>
    public ICustomLoopReceiptRetentionPort CreateReceiptRetentionPort(CustomLoopReceiptArtifactClass artifactClass) => new CustomLoopDefinitionRetentionPort(this, artifactClass);

    internal Task<CustomLoopReceiptCleanupResult> CleanupReceiptRetentionAsync(CustomLoopReceiptCleanupCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            CustomLoopReceiptRetentionContractValidator.ValidateCleanupCommand(command);
            RequireAuthoringArtifactClass(command.ArtifactClass);
            var observedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime();
            return CleanupReceiptRetentionAsync(CustomLoopReceiptCleanupRequestFactory.Create(command, observedAtUtc), allowPersistedTimeReuse: true, cancellationToken);
        }
        catch (ArgumentException exception)
        {
            return Task.FromResult(CleanupResult(CustomLoopReceiptCleanupStatus.Invalid, null, detail: exception.Message));
        }
    }

    /// <summary>
    /// Inspects bounded receipt usage, exact replay, compact proof, and cleanup posture for one authoring class.
    /// </summary>
    /// <param name="artifactClass">The definition-mutation receipt or definition-tombstone class.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The validated class posture.</returns>
    public async Task<CustomLoopReceiptClassPosture> InspectReceiptRetentionAsync(CustomLoopReceiptArtifactClass artifactClass, CancellationToken cancellationToken = default)
    {
        RequireAuthoringArtifactClass(artifactClass);
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            using var workspaceLock = _pathGuard.AcquireExclusiveMutationLock(_paths.LoopDefinitionsPath);
            using var retentionLock = _pathGuard.AcquireExclusiveMutationLock(_paths.CustomLoopReceiptRetentionPath);
            return await InspectReceiptRetentionUnderLockAsync(artifactClass, cancellationToken);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>
    /// Measures the canonical active cleanup journal retained for one authoring receipt class.
    /// </summary>
    /// <param name="artifactClass">The definition-mutation receipt or definition-tombstone class.</param>
    /// <param name="cancellationToken">The token used to cancel storage inspection.</param>
    /// <returns>The serialized UTF-8 byte count and durable state, or an empty posture when no active journal exists.</returns>
    /// <remarks>
    /// This is an accounting-only inspection boundary. It preserves the journal and uses the same workspace lock and
    /// strict schema validation as cleanup so an interface never needs to inspect retention files directly.
    /// </remarks>
    public async Task<CustomLoopReceiptActiveCleanupJournalPosture> InspectActiveReceiptCleanupJournalAsync(CustomLoopReceiptArtifactClass artifactClass, CancellationToken cancellationToken = default)
    {
        RequireAuthoringArtifactClass(artifactClass);
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            using var workspaceLock = _pathGuard.AcquireExclusiveMutationLock(_paths.LoopDefinitionsPath);
            using var retentionLock = _pathGuard.AcquireExclusiveMutationLock(_paths.CustomLoopReceiptRetentionPath);
            var journal = await ReadCleanupJournalAsync(artifactClass, cancellationToken);
            return journal is null
                ? new CustomLoopReceiptActiveCleanupJournalPosture(0, null, null, null)
                : new CustomLoopReceiptActiveCleanupJournalPosture(CustomLoopReceiptRetentionContractCodec.SerializeCleanupJournal(journal).LongLength, journal.Stage, journal.Outcome, IsTerminal(journal.Stage) ? null : journal.OwnershipAcquiredAtUtc + CustomLoopReceiptRetentionPolicy.CleanupOwnershipWindow);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>
    /// Distinguishes an exact authoring receipt from compact expiry proof and an unknown operation identity.
    /// </summary>
    /// <param name="artifactClass">The definition-mutation receipt or definition-tombstone class.</param>
    /// <param name="operationId">The authoring operation identity.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The exact, expired, or unknown lookup result.</returns>
    public async Task<CustomLoopReceiptOperationLookupResult> LookupReceiptOperationAsync(CustomLoopReceiptArtifactClass artifactClass, string operationId, CancellationToken cancellationToken = default)
    {
        RequireAuthoringArtifactClass(artifactClass);
        var safeOperationId = CustomLoopArtifactIdentifier.Require(operationId, nameof(operationId), CustomLoopLimits.MaxMutationOperationIdCharacters);
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            using var workspaceLock = _pathGuard.AcquireExclusiveMutationLock(_paths.LoopDefinitionsPath);
            using var retentionLock = _pathGuard.AcquireExclusiveMutationLock(_paths.CustomLoopReceiptRetentionPath);
            var operationPath = GetOperationPath(safeOperationId);
            if (File.Exists(operationPath))
            {
                var artifact = await ReadRetentionArtifactAsync(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, operationPath, cancellationToken);
                if (artifactClass == CustomLoopReceiptArtifactClass.DefinitionMutationReceipt
                    || artifact.Operation is { Kind: CustomLoopDefinitionMutationKind.Delete, Outcome: CustomLoopDefinitionStoreStatus.Deleted })
                {
                    var ledgerWithExact = await ReadProofLedgerAsync(cancellationToken);
                    var retainedProof = ledgerWithExact?.ExpiredOperations.SingleOrDefault(item => item.ArtifactClass == CustomLoopReceiptArtifactClass.DefinitionMutationReceipt && string.Equals(item.OperationId, safeOperationId, StringComparison.Ordinal));
                    var retainedLineage = ledgerWithExact?.DefinitionLineage.SingleOrDefault(item => string.Equals(item.LastMutationOperationId, safeOperationId, StringComparison.Ordinal));
                    if (retainedProof is not null && !ProofMatchesOperation(retainedProof, artifact, retainedLineage))
                    {
                        throw new FormatException($"Exact receipt `{safeOperationId}` conflicts with its retained compact proof.");
                    }

                    var exact = new CustomLoopReceiptOperationLookupResult(artifactClass, safeOperationId, CustomLoopReceiptOperationLookupStatus.Exact, null, "The complete authoring receipt remains available for exact replay.");
                    CustomLoopReceiptRetentionContractValidator.ValidateLookupResult(exact);
                    return exact;
                }
            }

            var ledger = await ReadProofLedgerAsync(cancellationToken);
            var proof = ledger?.ExpiredOperations.SingleOrDefault(item => item.ArtifactClass == CustomLoopReceiptArtifactClass.DefinitionMutationReceipt
                && string.Equals(item.OperationId, safeOperationId, StringComparison.Ordinal)
                && (artifactClass == CustomLoopReceiptArtifactClass.DefinitionMutationReceipt
                    || item is { DefinitionMutationKind: CustomLoopDefinitionMutationKind.Delete, DefinitionMutationOutcome: CustomLoopDefinitionStoreStatus.Deleted }));
            var result = proof is null
                ? new CustomLoopReceiptOperationLookupResult(artifactClass, safeOperationId, CustomLoopReceiptOperationLookupStatus.Unknown, null, "No full receipt or compact expiry proof recognizes this operation identity.")
                : new CustomLoopReceiptOperationLookupResult(artifactClass, safeOperationId, CustomLoopReceiptOperationLookupStatus.Expired, proof, "Exact replay expired; compact proof permanently reserves this operation identity.");
            CustomLoopReceiptRetentionContractValidator.ValidateLookupResult(result);
            return result;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>
    /// Executes or recovers one bounded, audited authoring-receipt cleanup journal.
    /// </summary>
    /// <param name="request">The governed cleanup request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The visible cleanup result.</returns>
    public Task<CustomLoopReceiptCleanupResult> CleanupReceiptRetentionAsync(CustomLoopReceiptCleanupRequest request, CancellationToken cancellationToken = default)
    {
        return CleanupReceiptRetentionAsync(request, allowPersistedTimeReuse: false, cancellationToken);
    }

    private async Task<CustomLoopReceiptCleanupResult> CleanupReceiptRetentionAsync(CustomLoopReceiptCleanupRequest request, bool allowPersistedTimeReuse, CancellationToken cancellationToken)
    {
        try
        {
            CustomLoopReceiptRetentionContractValidator.ValidateCleanupRequest(request);
            RequireAuthoringArtifactClass(request.ArtifactClass);
        }
        catch (ArgumentException exception)
        {
            return CleanupResult(CustomLoopReceiptCleanupStatus.Invalid, null, detail: exception.Message);
        }

        if (!await _mutationGate.WaitAsync(0, cancellationToken))
        {
            return CleanupResult(CustomLoopReceiptCleanupStatus.OperationInProgress, null, blockReason: CustomLoopReceiptCleanupBlockReason.OwnershipUnresolved, detail: "Another in-process authoring mutation or cleanup owns the bounded workspace window.");
        }

        try
        {
            try
            {
                using var workspaceLock = _pathGuard.AcquireExclusiveMutationLock(_paths.LoopDefinitionsPath);
                using var retentionLock = _pathGuard.AcquireExclusiveMutationLock(_paths.CustomLoopReceiptRetentionPath);
                return await CleanupReceiptRetentionUnderLockAsync(request, allowPersistedTimeReuse, cancellationToken);
            }
            catch (InvalidOperationException exception) when (exception.InnerException is IOException)
            {
                return CleanupResult(CustomLoopReceiptCleanupStatus.OperationInProgress, null, blockReason: CustomLoopReceiptCleanupBlockReason.OwnershipUnresolved, detail: "Another process owns the custom-loop authoring mutation or cleanup window.");
            }
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task<CustomLoopReceiptClassPosture> InspectReceiptRetentionUnderLockAsync(CustomLoopReceiptArtifactClass artifactClass, CancellationToken cancellationToken)
    {
        var usage = Enum.GetValues<CustomLoopReceiptArtifactCategory>()
            .Where(category => category != CustomLoopReceiptArtifactCategory.Unknown)
            .ToDictionary(category => category, _ => (Count: 0, Bytes: 0L));
        var liveExpiries = new List<DateTimeOffset>();
        var budget = CustomLoopReceiptRetentionPolicy.GetBudget(artifactClass);
        var blockReason = CustomLoopReceiptCleanupBlockReason.None;
        var cleanupHistoryCount = 0;
        var cleanupHistoryBytes = 0L;
        var observedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        IReadOnlyList<CustomLoopDefinitionRetentionArtifact> operations = [];
        CustomLoopReceiptProofLedger? ledger = null;

        try
        {
            operations = await ReadRetentionArtifactsAsync(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, cancellationToken);
            ledger = await ReadProofLedgerAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is FormatException or IOException or UnauthorizedAccessException)
        {
            blockReason = CustomLoopReceiptCleanupBlockReason.CorruptEvidence;
            AddUsage(usage, CustomLoopReceiptArtifactCategory.Corrupt, 1);
        }

        try
        {
            var history = new CustomLoopReceiptCleanupHistoryStore(_pathGuard, GetCleanupHistoryRoot(artifactClass), artifactClass);
            (cleanupHistoryCount, cleanupHistoryBytes) = await history.InspectAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is FormatException or IOException or UnauthorizedAccessException)
        {
            blockReason = CustomLoopReceiptCleanupBlockReason.CorruptEvidence;
            AddUsage(usage, CustomLoopReceiptArtifactCategory.Corrupt, 1);
        }

        var liveLoopIds = (await ReadDefinitionsAsync(cancellationToken)).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);

        IReadOnlyList<CustomLoopDefinitionRetentionArtifact> rawClass;
        if (artifactClass == CustomLoopReceiptArtifactClass.DefinitionMutationReceipt)
        {
            rawClass = operations;
        }
        else
        {
            var tombstones = await ReadTombstonePostureArtifactsAsync(cancellationToken);
            rawClass = tombstones.Artifacts;
            if (tombstones.Corrupt)
            {
                AddUsage(usage, CustomLoopReceiptArtifactCategory.Corrupt, 1);
                blockReason = MergeBlockReason(blockReason, CustomLoopReceiptArtifactCategory.Corrupt);
            }
        }
        var operationsById = operations.Where(item => item.Operation is not null).ToDictionary(item => item.ArtifactId, StringComparer.Ordinal);
        foreach (var artifact in rawClass)
        {
            var category = artifactClass == CustomLoopReceiptArtifactClass.DefinitionMutationReceipt
                ? ClassifyOperation(artifact, ledger, liveLoopIds, observedAtUtc, liveExpiries)
                : ClassifyTombstone(artifact.Tombstone!, operationsById, ledger, observedAtUtc, liveExpiries);
            AddUsage(usage, category, artifact.Utf8Json.LongLength);
            blockReason = MergeBlockReason(blockReason, category);
        }

        if (ledger is not null)
        {
            if (artifactClass == CustomLoopReceiptArtifactClass.DefinitionMutationReceipt)
            {
                AddProofUsage(usage, CustomLoopReceiptArtifactCategory.ExpiredIdempotency, ledger.ExpiredOperations.Where(item => item.ArtifactClass == artifactClass), CustomLoopReceiptRetentionContractCodec.MeasureExpiredOperationProofUtf8Bytes);
            }
            else
            {
                AddProofUsage(usage, CustomLoopReceiptArtifactCategory.RetainedLineage, ledger.DefinitionLineage, CustomLoopReceiptRetentionContractCodec.MeasureDefinitionLineageProofUtf8Bytes);
            }
        }

        var categories = usage.Select(item => new CustomLoopReceiptCategoryUsage(item.Key, item.Value.Count, item.Value.Bytes)).ToImmutableArray();
        var artifactCount = categories.Where(item => item.Category is not CustomLoopReceiptArtifactCategory.RetainedLineage and not CustomLoopReceiptArtifactCategory.ExpiredIdempotency).Sum(item => item.ArtifactCount);
        var artifactBytes = categories.Where(item => item.Category is not CustomLoopReceiptArtifactCategory.RetainedLineage and not CustomLoopReceiptArtifactCategory.ExpiredIdempotency).Sum(item => item.Utf8Bytes);
        var proofCount = categories.Where(item => item.Category is CustomLoopReceiptArtifactCategory.RetainedLineage or CustomLoopReceiptArtifactCategory.ExpiredIdempotency).Sum(item => item.ArtifactCount);
        var proofBytes = categories.Where(item => item.Category is CustomLoopReceiptArtifactCategory.RetainedLineage or CustomLoopReceiptArtifactCategory.ExpiredIdempotency).Sum(item => item.Utf8Bytes);
        var maximumNewArtifactBytes = artifactClass == CustomLoopReceiptArtifactClass.DefinitionMutationReceipt ? MaxDefinitionMutationOperationArtifactBytes : MaxTombstoneArtifactBytes;
        var exhaustion = GetExhaustionReason(budget, artifactCount, artifactBytes, maximumNewArtifactBytes, proofCount, proofBytes);
        if (exhaustion == CustomLoopReceiptQuotaExhaustionReason.None && cleanupHistoryCount >= CustomLoopReceiptRetentionPolicy.MaxCleanupHistoryEntryCount)
        {
            exhaustion = CustomLoopReceiptQuotaExhaustionReason.CleanupHistoryCountLimit;
            blockReason = CustomLoopReceiptCleanupBlockReason.CleanupHistoryCapacityExhausted;
        }
        else if (exhaustion == CustomLoopReceiptQuotaExhaustionReason.None && cleanupHistoryBytes >= CustomLoopReceiptRetentionPolicy.MaxCleanupHistoryUtf8Bytes)
        {
            exhaustion = CustomLoopReceiptQuotaExhaustionReason.CleanupHistoryByteLimit;
            blockReason = CustomLoopReceiptCleanupBlockReason.CleanupHistoryCapacityExhausted;
        }

        var posture = new CustomLoopReceiptClassPosture(
            artifactClass,
            budget,
            categories,
            liveExpiries.Count == 0 ? null : liveExpiries.Min(),
            liveExpiries.Count == 0 ? null : liveExpiries.Max(),
            cleanupHistoryCount,
            cleanupHistoryBytes,
            exhaustion,
            blockReason,
            blockReason == CustomLoopReceiptCleanupBlockReason.None ? "Authoring receipt retention is bounded and recoverable." : "Authoring receipt retention contains evidence that cleanup must preserve for review.");
        CustomLoopReceiptRetentionContractValidator.ValidateClassPosture(posture);
        return posture;
    }

    private async Task<(IReadOnlyList<CustomLoopDefinitionRetentionArtifact> Artifacts, bool Corrupt)> ReadTombstonePostureArtifactsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return (await ReadRetentionArtifactsAsync(CustomLoopReceiptArtifactClass.DefinitionTombstone, cancellationToken), false);
        }
        catch (Exception exception) when (exception is FormatException or IOException or UnauthorizedAccessException)
        {
            return ([], true);
        }
    }

    private async Task<CustomLoopReceiptCleanupResult> CleanupReceiptRetentionUnderLockAsync(CustomLoopReceiptCleanupRequest request, bool allowPersistedTimeReuse, CancellationToken cancellationToken)
    {
        var history = new CustomLoopReceiptCleanupHistoryStore(_pathGuard, GetCleanupHistoryRoot(request.ArtifactClass), request.ArtifactClass);
        CustomLoopReceiptCleanupJournal? archived;
        try
        {
            archived = await history.ReadAsync(request.OperationId, cancellationToken);
        }
        catch (Exception exception) when (exception is FormatException or IOException or UnauthorizedAccessException)
        {
            return CleanupResult(CustomLoopReceiptCleanupStatus.Corrupt, null, blockReason: CustomLoopReceiptCleanupBlockReason.CorruptEvidence, detail: $"Cleanup history is corrupt or unreadable: {exception.GetType().Name}.");
        }

        if (archived is not null)
        {
            if (allowPersistedTimeReuse && MatchesCleanupCommand(archived.Request, request))
            {
                request = archived.Request;
            }

            return string.Equals(archived.RequestHash, CustomLoopReceiptRetentionContractCodec.ComputeCleanupRequestHash(request), StringComparison.Ordinal)
                ? MapTerminalJournal(archived, replay: true)
                : CleanupResult(CustomLoopReceiptCleanupStatus.Invalid, archived, detail: "Cleanup operation identity was reused with different canonical command content.");
        }

        CustomLoopReceiptCleanupJournal? journal;
        try
        {
            journal = await ReadCleanupJournalAsync(request.ArtifactClass, cancellationToken);
        }
        catch (Exception exception) when (exception is FormatException or IOException or UnauthorizedAccessException)
        {
            return CleanupResult(CustomLoopReceiptCleanupStatus.Corrupt, null, blockReason: CustomLoopReceiptCleanupBlockReason.CorruptEvidence, detail: $"Cleanup journal is corrupt or unreadable: {exception.GetType().Name}.");
        }

        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        if (journal is not null && string.Equals(journal.Request.OperationId, request.OperationId, StringComparison.Ordinal) && allowPersistedTimeReuse && MatchesCleanupCommand(journal.Request, request))
        {
            request = journal.Request;
        }

        if (journal is not null && !IsTerminal(journal.Stage))
        {
            var sameRequest = string.Equals(journal.Request.OperationId, request.OperationId, StringComparison.Ordinal)
                && string.Equals(journal.RequestHash, CustomLoopReceiptRetentionContractCodec.ComputeCleanupRequestHash(request), StringComparison.Ordinal);
            if (string.Equals(journal.Request.OperationId, request.OperationId, StringComparison.Ordinal) && !sameRequest)
            {
                return CleanupResult(CustomLoopReceiptCleanupStatus.Invalid, journal, detail: "Cleanup operation identity was reused with a different canonical request.");
            }

            if (now < journal.OwnershipAcquiredAtUtc + CustomLoopReceiptRetentionPolicy.CleanupOwnershipWindow)
            {
                return CleanupResult(CustomLoopReceiptCleanupStatus.OperationInProgress, journal, blockReason: CustomLoopReceiptCleanupBlockReason.OwnershipUnresolved, detail: "Another process owns the bounded cleanup window.");
            }

            journal = journal with
            {
                OwnerGenerationId = $"cleanup-owner-{Guid.NewGuid():N}",
                OwnerProcessId = Environment.ProcessId,
                OwnershipAcquiredAtUtc = now,
                UpdatedAtUtc = now
            };
            await WriteCleanupJournalAsync(journal, cancellationToken);
            return await ResumeCleanupAsync(journal, recovering: true, cancellationToken);
        }

        if (journal is not null && string.Equals(journal.Request.OperationId, request.OperationId, StringComparison.Ordinal))
        {
            if (!string.Equals(journal.RequestHash, CustomLoopReceiptRetentionContractCodec.ComputeCleanupRequestHash(request), StringComparison.Ordinal))
            {
                return CleanupResult(CustomLoopReceiptCleanupStatus.Invalid, journal, detail: "Cleanup operation identity was reused with a different canonical request.");
            }

            return MapTerminalJournal(journal, replay: true);
        }

        if (journal is not null && IsTerminal(journal.Stage))
        {
            CustomLoopReceiptQuotaExhaustionReason archiveExhaustion;
            try
            {
                archiveExhaustion = await history.ArchiveAsync(journal, cancellationToken);
            }
            catch (Exception exception) when (exception is FormatException or IOException or UnauthorizedAccessException)
            {
                return CleanupResult(CustomLoopReceiptCleanupStatus.Corrupt, journal, blockReason: CustomLoopReceiptCleanupBlockReason.CorruptEvidence, detail: $"Completed cleanup history could not preserve the prior operation identity: {exception.GetType().Name}.");
            }

            if (archiveExhaustion != CustomLoopReceiptQuotaExhaustionReason.None)
            {
                return CleanupResult(CustomLoopReceiptCleanupStatus.QuotaExhausted, journal, exhaustionReason: archiveExhaustion, blockReason: CustomLoopReceiptCleanupBlockReason.CleanupHistoryCapacityExhausted, detail: "Completed cleanup-operation history is full, so the prior identity was preserved and no new cleanup began.");
            }
        }

        IReadOnlyList<CustomLoopReceiptCleanupCandidate> candidates;
        try
        {
            candidates = await SelectCleanupCandidatesAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is FormatException or IOException or UnauthorizedAccessException)
        {
            return CleanupResult(CustomLoopReceiptCleanupStatus.Corrupt, null, blockReason: CustomLoopReceiptCleanupBlockReason.CorruptEvidence, detail: $"Raw authoring evidence is corrupt or ambiguous: {exception.GetType().Name}.");
        }

        var acquiredAt = _timeProvider.GetUtcNow().ToUniversalTime();
        journal = new CustomLoopReceiptCleanupJournal(
            CustomLoopReceiptCleanupJournal.CurrentSchemaVersion,
            request,
            CustomLoopReceiptRetentionContractCodec.ComputeCleanupRequestHash(request),
            $"cleanup-owner-{Guid.NewGuid():N}",
            Environment.ProcessId,
            acquiredAt,
            CustomLoopReceiptCleanupStage.IntentPersisted,
            CustomLoopReceiptCleanupOutcome.Unknown,
            acquiredAt,
            candidates.ToImmutableArray(),
            null,
            0,
            0,
            candidates.Count == 0 ? "No safely compactable evidence was selected." : "Immutable cleanup candidates are durable before mutation.");
        await WriteCleanupJournalAsync(journal, cancellationToken);
        return await ResumeCleanupAsync(journal, recovering: false, cancellationToken);
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

    private async Task<CustomLoopReceiptCleanupResult> ResumeCleanupAsync(CustomLoopReceiptCleanupJournal journal, bool recovering, CancellationToken cancellationToken)
    {
        var uncertainIntentAudit = recovering && journal.Stage == CustomLoopReceiptCleanupStage.IntentAuditStarted;
        var uncertainOutcomeAudit = recovering && journal.Stage == CustomLoopReceiptCleanupStage.OutcomeAuditStarted;
        using var ownerWindow = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var remainingOwnership = journal.OwnershipAcquiredAtUtc + CustomLoopReceiptRetentionPolicy.CleanupOwnershipWindow - _timeProvider.GetUtcNow().ToUniversalTime();
        if (remainingOwnership <= TimeSpan.Zero)
        {
            return CleanupResult(CustomLoopReceiptCleanupStatus.OperationInProgress, journal, blockReason: CustomLoopReceiptCleanupBlockReason.OwnershipUnresolved, detail: "The cleanup ownership window expired before recovery could advance.");
        }

        ownerWindow.CancelAfter(remainingOwnership > TimeSpan.FromSeconds(1) ? remainingOwnership - TimeSpan.FromSeconds(1) : remainingOwnership);
        var ownerToken = ownerWindow.Token;
        if (journal.Stage == CustomLoopReceiptCleanupStage.IntentPersisted)
        {
            journal = await WriteJournalStageAsync(journal, CustomLoopReceiptCleanupStage.IntentAuditStarted, CustomLoopReceiptCleanupOutcome.Unknown, "The single bounded intent-audit attempt is durably marked.", ownerToken);
            if (!await TryAppendCleanupAuditAsync(journal, intent: true, AuditSchema.Outcomes.Requested, ownerToken, cancellationToken))
            {
                journal = await WriteJournalStageAsync(journal, CustomLoopReceiptCleanupStage.Degraded, CustomLoopReceiptCleanupOutcome.AuditUnavailable, "Cleanup intent audit was unavailable; no evidence was mutated.", cancellationToken);
                return CleanupResult(CustomLoopReceiptCleanupStatus.AuditUnavailable, journal, blockReason: CustomLoopReceiptCleanupBlockReason.AuditUnavailable, detail: journal.Detail);
            }

            if (journal.Candidates.Length == 0)
            {
                journal = await WriteJournalStageAsync(journal, CustomLoopReceiptCleanupStage.Completed, CustomLoopReceiptCleanupOutcome.NothingEligible, "No complete audited evidence exists outside the exact replay horizon.", ownerToken);
                return CleanupResult(CustomLoopReceiptCleanupStatus.NothingEligible, journal, detail: journal.Detail);
            }

            journal = await WriteJournalStageAsync(journal, CustomLoopReceiptCleanupStage.IntentAuditRecorded, CustomLoopReceiptCleanupOutcome.Unknown, "Cleanup intent audit is durable.", ownerToken);
        }

        if (uncertainIntentAudit)
        {
            journal = await WriteJournalStageAsync(journal, CustomLoopReceiptCleanupStage.Degraded, CustomLoopReceiptCleanupOutcome.AuditUnavailable, "An interrupted intent-audit attempt was not duplicated; no evidence was mutated.", ownerToken);
            return CleanupResult(CustomLoopReceiptCleanupStatus.AuditUnavailable, journal, blockReason: CustomLoopReceiptCleanupBlockReason.AuditUnavailable, detail: journal.Detail);
        }

        if (journal.Stage == CustomLoopReceiptCleanupStage.IntentAuditRecorded)
        {
            CustomLoopReceiptProofLedger ledger;
            try
            {
                ledger = await MergeAndWriteProofLedgerAsync(journal, ownerToken);
            }
            catch (ArgumentException exception)
            {
                return CleanupResult(CustomLoopReceiptCleanupStatus.QuotaExhausted, journal, exhaustionReason: GetProofExhaustion(exception), blockReason: CustomLoopReceiptCleanupBlockReason.ProofCapacityExhausted, detail: "Compact proof has no capacity for every required identity and lineage record.");
            }
            catch (Exception exception) when (exception is FormatException or IOException or UnauthorizedAccessException)
            {
                journal = await WriteJournalStageAsync(journal, CustomLoopReceiptCleanupStage.Degraded, CustomLoopReceiptCleanupOutcome.Corrupt, "Compact proof could not be validated or written safely.", ownerToken);
                return CleanupResult(CustomLoopReceiptCleanupStatus.Corrupt, journal, blockReason: CustomLoopReceiptCleanupBlockReason.CorruptEvidence, detail: $"Compact proof failed closed: {exception.GetType().Name}.");
            }

            journal = journal with { ProofLedgerHash = CustomLoopReceiptRetentionContractCodec.ComputeProofLedgerHash(ledger) };
            journal = await WriteJournalStageAsync(journal, CustomLoopReceiptCleanupStage.ProofLedgerWritten, CustomLoopReceiptCleanupOutcome.Unknown, "Replacement compact proof is durable and verified.", ownerToken);
        }

        if (journal.Stage == CustomLoopReceiptCleanupStage.ProofLedgerWritten)
        {
            try
            {
                if (!await ProofLedgerMatchesJournalAsync(journal, ownerToken))
                {
                    journal = await WriteJournalStageAsync(journal, CustomLoopReceiptCleanupStage.Degraded, CustomLoopReceiptCleanupOutcome.Corrupt, "Committed compact proof is missing, changed, or incomplete; every remaining raw artifact was preserved.", ownerToken);
                    return CleanupResult(CustomLoopReceiptCleanupStatus.Corrupt, journal, blockReason: CustomLoopReceiptCleanupBlockReason.CorruptEvidence, detail: journal.Detail);
                }
            }
            catch (Exception exception) when (exception is FormatException or IOException or UnauthorizedAccessException)
            {
                journal = await WriteJournalStageAsync(journal, CustomLoopReceiptCleanupStage.Degraded, CustomLoopReceiptCleanupOutcome.Corrupt, "Committed compact proof could not be revalidated before raw evidence removal.", ownerToken);
                return CleanupResult(CustomLoopReceiptCleanupStatus.Corrupt, journal, blockReason: CustomLoopReceiptCleanupBlockReason.CorruptEvidence, detail: $"Compact proof failed closed: {exception.GetType().Name}.");
            }

            (bool IsCanonical, bool HasConflict, int AttributedRemovedCount, long AttributedRemovedBytes) removalProgress;
            try
            {
                removalProgress = await ReconcileRemovalProgressAsync(journal, ownerToken);
            }
            catch (Exception exception) when (exception is FormatException or IOException or UnauthorizedAccessException)
            {
                journal = await WriteJournalStageAsync(journal, CustomLoopReceiptCleanupStage.Degraded, CustomLoopReceiptCleanupOutcome.Corrupt, "Authoring evidence could not be read while reconstructing removal progress; every remaining raw artifact was preserved.", ownerToken);
                return CleanupResult(CustomLoopReceiptCleanupStatus.Corrupt, journal, blockReason: CustomLoopReceiptCleanupBlockReason.CorruptEvidence, detail: $"Removal progress failed closed: {exception.GetType().Name}.");
            }

            if (!removalProgress.IsCanonical)
            {
                journal = journal with { RemovedArtifactCount = removalProgress.AttributedRemovedCount, RemovedArtifactUtf8Bytes = removalProgress.AttributedRemovedBytes };
                if (removalProgress.HasConflict)
                {
                    journal = await WriteJournalStageAsync(journal, CustomLoopReceiptCleanupStage.AbandonedConflict, CustomLoopReceiptCleanupOutcome.Conflict, "A retained authoring artifact changed after durable intent; exact prior removal progress is preserved and no additional artifact was removed.", ownerToken);
                    return CleanupResult(CustomLoopReceiptCleanupStatus.CleanupConflict, journal, blockReason: CustomLoopReceiptCleanupBlockReason.CleanupConflict, detail: journal.Detail);
                }

                journal = await WriteJournalStageAsync(journal, CustomLoopReceiptCleanupStage.Degraded, CustomLoopReceiptCleanupOutcome.Degraded, "Authoring evidence no longer forms the canonical removal prefix after proof commit; exact attributable progress is preserved and cleanup requires review.", ownerToken);
                return CleanupResult(CustomLoopReceiptCleanupStatus.Degraded, journal, blockReason: CustomLoopReceiptCleanupBlockReason.AmbiguousEvidence, detail: journal.Detail);
            }

            if (removalProgress.AttributedRemovedCount > journal.RemovedArtifactCount)
            {
                journal = journal with { RemovedArtifactCount = removalProgress.AttributedRemovedCount, RemovedArtifactUtf8Bytes = removalProgress.AttributedRemovedBytes };
                journal = await WriteJournalStageAsync(journal, CustomLoopReceiptCleanupStage.ProofLedgerWritten, CustomLoopReceiptCleanupOutcome.Unknown, "A canonical missing authoring-evidence prefix was reconstructed as exact attributed progress after an interrupted removal write.", ownerToken);
            }

            var canonicalCandidates = journal.Candidates.OrderBy(item => item.ArtifactId, StringComparer.Ordinal).ToArray();
            var removedCount = journal.RemovedArtifactCount;
            var removedBytes = journal.RemovedArtifactUtf8Bytes;
            foreach (var candidate in canonicalCandidates.Skip(removedCount))
            {
                bool removed;
                try
                {
                    removed = await RemoveCandidateWithVerifiedTransitionAsync(journal, candidate, ownerToken);
                }
                catch (Exception exception) when (exception is FormatException or IOException or UnauthorizedAccessException)
                {
                    journal = await WriteJournalStageAsync(journal, CustomLoopReceiptCleanupStage.Degraded, CustomLoopReceiptCleanupOutcome.Corrupt, "One selected authoring artifact could not complete its identity-bound removal transition; no later candidate was removed.", ownerToken);
                    return CleanupResult(CustomLoopReceiptCleanupStatus.Corrupt, journal, blockReason: CustomLoopReceiptCleanupBlockReason.CorruptEvidence, detail: $"Candidate removal failed closed: {exception.GetType().Name}.");
                }

                if (!removed)
                {
                    journal = await WriteJournalStageAsync(journal, CustomLoopReceiptCleanupStage.AbandonedConflict, CustomLoopReceiptCleanupOutcome.Conflict, "A source or pending-removal artifact changed during the identity-bound removal transition; exact durable progress is preserved and no later candidate was removed.", ownerToken);
                    return CleanupResult(CustomLoopReceiptCleanupStatus.CleanupConflict, journal, blockReason: CustomLoopReceiptCleanupBlockReason.CleanupConflict, detail: journal.Detail);
                }

                removedCount++;
                removedBytes = checked(removedBytes + candidate.ArtifactUtf8Bytes);
                journal = journal with { RemovedArtifactCount = removedCount, RemovedArtifactUtf8Bytes = removedBytes };
                journal = await WriteJournalStageAsync(journal, CustomLoopReceiptCleanupStage.ProofLedgerWritten, CustomLoopReceiptCleanupOutcome.Unknown, "One canonical authoring-evidence removal is durably attributed within the immutable cleanup batch.", ownerToken);
            }

            journal = await WriteJournalStageAsync(journal, CustomLoopReceiptCleanupStage.ArtifactsRemoved, CustomLoopReceiptCleanupOutcome.Unknown, "Every selected raw artifact was hash-revalidated and removed.", ownerToken);
        }

        if (journal.Stage == CustomLoopReceiptCleanupStage.ArtifactsRemoved)
        {
            journal = await WriteJournalStageAsync(journal, CustomLoopReceiptCleanupStage.OutcomeAuditStarted, CustomLoopReceiptCleanupOutcome.Unknown, "The single bounded outcome-audit attempt is durably marked.", ownerToken);
        }

        if (journal.Stage == CustomLoopReceiptCleanupStage.OutcomeAuditStarted)
        {
            if (uncertainOutcomeAudit)
            {
                journal = await WriteJournalStageAsync(journal, CustomLoopReceiptCleanupStage.CommittedWithAuditWarning, CustomLoopReceiptCleanupOutcome.AuditUnavailable, "Raw evidence was compacted; an interrupted outcome-audit attempt was not duplicated.", ownerToken);
                return CleanupResult(CustomLoopReceiptCleanupStatus.CommittedWithAuditWarning, journal, blockReason: CustomLoopReceiptCleanupBlockReason.AuditUnavailable, detail: journal.Detail);
            }

            if (!await TryAppendCleanupAuditAsync(journal, intent: false, AuditSchema.Outcomes.Succeeded, ownerToken, cancellationToken))
            {
                journal = await WriteJournalStageAsync(journal, CustomLoopReceiptCleanupStage.CommittedWithAuditWarning, CustomLoopReceiptCleanupOutcome.AuditUnavailable, "Raw evidence was compacted, but the bounded outcome-audit attempt failed and will not be duplicated.", cancellationToken);
                return CleanupResult(CustomLoopReceiptCleanupStatus.CommittedWithAuditWarning, journal, blockReason: CustomLoopReceiptCleanupBlockReason.AuditUnavailable, detail: journal.Detail);
            }

            journal = await WriteJournalStageAsync(journal, CustomLoopReceiptCleanupStage.Completed, CustomLoopReceiptCleanupOutcome.Succeeded, "Expired complete authoring evidence was replaced by compact proof.", ownerToken);
        }

        return MapTerminalJournal(journal, replay: false);
    }

    private async Task<IReadOnlyList<CustomLoopReceiptCleanupCandidate>> SelectCleanupCandidatesAsync(CustomLoopReceiptCleanupRequest request, CancellationToken cancellationToken)
    {
        var operations = await ReadRetentionArtifactsAsync(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, cancellationToken);
        var ledger = await ReadProofLedgerAsync(cancellationToken);
        var candidates = new List<CustomLoopReceiptCleanupCandidate>();
        long bytes = 0;
        if (request.ArtifactClass == CustomLoopReceiptArtifactClass.DefinitionMutationReceipt)
        {
            var liveLoopIds = (await ReadDefinitionsAsync(cancellationToken)).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var artifact in operations.OrderBy(item => item.Operation!.UpdatedAtUtc).ThenBy(item => item.ArtifactId, StringComparer.Ordinal))
            {
                var operation = artifact.Operation!;
                if (operation.State != CustomLoopDefinitionMutationState.OutcomeCommitted || !operation.OutcomeAuditRecorded || operation.UpdatedAtUtc > request.ReplayCutoffUtc)
                {
                    continue;
                }

                if (operation.Kind == CustomLoopDefinitionMutationKind.Create
                    && (liveLoopIds.Contains(operation.LoopId)
                        || ledger?.DefinitionLineage.Any(item => string.Equals(item.LoopId, operation.LoopId, StringComparison.Ordinal)) != true))
                {
                    continue;
                }

                var lineage = operation.Kind == CustomLoopDefinitionMutationKind.Delete && operation.ResultTombstone is { } tombstone
                    ? ToLineageProof(operation, tombstone)
                    : null;
                var proof = ToExpiredProof(operation, artifact.Hash, lineage);
                if (!TryAddCandidate(request, candidates, ref bytes, new CustomLoopReceiptCleanupCandidate(artifact.ArtifactId, artifact.Hash, artifact.Utf8Json.LongLength, CustomLoopReceiptArtifactCategory.Compactable, true, true, proof, lineage)))
                {
                    break;
                }
            }
        }
        else
        {
            var byOperation = operations.ToDictionary(item => item.ArtifactId, StringComparer.Ordinal);
            foreach (var artifact in (await ReadRetentionArtifactsAsync(CustomLoopReceiptArtifactClass.DefinitionTombstone, cancellationToken)).OrderBy(item => item.Tombstone!.DeletedAtUtc).ThenBy(item => item.ArtifactId, StringComparer.Ordinal))
            {
                var tombstone = artifact.Tombstone!;
                CustomLoopExpiredOperationProof? proof = null;
                CustomLoopDefinitionLineageProof? lineage = null;
                if (byOperation.TryGetValue(tombstone.MutationOperationId, out var operationArtifact))
                {
                    var operation = operationArtifact.Operation!;
                    if (operation.State != CustomLoopDefinitionMutationState.OutcomeCommitted || !operation.OutcomeAuditRecorded || operation.UpdatedAtUtc > request.ReplayCutoffUtc || operation.Kind != CustomLoopDefinitionMutationKind.Delete)
                    {
                        continue;
                    }

                    lineage = ToLineageProof(operation, tombstone);
                    proof = ToExpiredProof(operation, operationArtifact.Hash, lineage);
                }
                else
                {
                    proof = ledger?.ExpiredOperations.SingleOrDefault(item => item.ArtifactClass == CustomLoopReceiptArtifactClass.DefinitionMutationReceipt && string.Equals(item.OperationId, tombstone.MutationOperationId, StringComparison.Ordinal));
                    lineage = ledger?.DefinitionLineage.SingleOrDefault(item => string.Equals(item.LoopId, tombstone.LoopId, StringComparison.Ordinal));
                    if (proof is null || lineage is null || proof.CompletedAtUtc > request.ReplayCutoffUtc || !LineageMatchesTombstone(lineage, tombstone))
                    {
                        continue;
                    }
                }

                if (!TryAddCandidate(request, candidates, ref bytes, new CustomLoopReceiptCleanupCandidate(artifact.ArtifactId, artifact.Hash, artifact.Utf8Json.LongLength, CustomLoopReceiptArtifactCategory.Compactable, true, true, proof, lineage)))
                {
                    break;
                }
            }
        }

        return candidates;
    }

    private async Task<CustomLoopReceiptProofLedger> MergeAndWriteProofLedgerAsync(CustomLoopReceiptCleanupJournal journal, CancellationToken cancellationToken)
    {
        var current = await ReadProofLedgerAsync(cancellationToken);
        var operations = current?.ExpiredOperations.ToDictionary(item => (item.ArtifactClass, item.OperationId)) ?? [];
        var lineage = current?.DefinitionLineage.ToDictionary(item => item.LoopId, StringComparer.Ordinal) ?? new Dictionary<string, CustomLoopDefinitionLineageProof>(StringComparer.Ordinal);
        foreach (var candidate in journal.Candidates)
        {
            var proof = candidate.ExpiredOperationProof!;
            if (operations.TryGetValue((proof.ArtifactClass, proof.OperationId), out var existingProof) && existingProof != proof)
            {
                throw new FormatException($"Compact proof for operation `{proof.OperationId}` conflicts with the selected raw artifact.");
            }

            operations[(proof.ArtifactClass, proof.OperationId)] = proof;
            if (candidate.DefinitionLineageProof is { } definitionLineage)
            {
                if (lineage.TryGetValue(definitionLineage.LoopId, out var existingLineage) && existingLineage != definitionLineage)
                {
                    throw new FormatException($"Compact lineage for loop `{definitionLineage.LoopId}` conflicts with the selected tombstone.");
                }

                lineage[definitionLineage.LoopId] = definitionLineage;
            }
        }

        var createdAt = UtcNow(journal.UpdatedAtUtc);
        var ledger = new CustomLoopReceiptProofLedger(
            CustomLoopReceiptProofLedger.CurrentSchemaVersion,
            (current?.Generation ?? 0) + 1,
            createdAt,
            current is null ? null : CustomLoopReceiptRetentionContractCodec.ComputeProofLedgerHash(current),
            lineage.Values.ToImmutableArray(),
            operations.Values.ToImmutableArray());
        var bytes = CustomLoopReceiptRetentionContractCodec.SerializeProofLedger(ledger);
        await _pathGuard.WriteTextAtomicallyAsync(_paths.CustomLoopReceiptRetentionPath, _paths.CustomLoopReceiptProofLedgerPath, Encoding.UTF8.GetString(bytes), cancellationToken);
        var verified = await ReadProofLedgerAsync(cancellationToken) ?? throw new FormatException("Compact proof ledger disappeared after its atomic write.");
        if (!CustomLoopReceiptRetentionContractCodec.ProofLedgersEqual(ledger, verified))
        {
            throw new FormatException("Compact proof ledger did not verify after its atomic write.");
        }

        return verified;
    }

    private async Task<bool> ProofLedgerMatchesJournalAsync(CustomLoopReceiptCleanupJournal journal, CancellationToken cancellationToken)
    {
        if (journal.ProofLedgerHash is null)
        {
            return false;
        }

        var ledger = await ReadProofLedgerAsync(cancellationToken);
        if (ledger is null || !string.Equals(CustomLoopReceiptRetentionContractCodec.ComputeProofLedgerHash(ledger), journal.ProofLedgerHash, StringComparison.Ordinal))
        {
            return false;
        }

        return journal.Candidates.All(candidate => candidate.ExpiredOperationProof is { } proof
            && ledger.ExpiredOperations.Contains(proof)
            && (candidate.DefinitionLineageProof is null || ledger.DefinitionLineage.Contains(candidate.DefinitionLineageProof)));
    }

    private async Task<CustomLoopReceiptProofLedger?> ReadProofLedgerAsync(CancellationToken cancellationToken)
    {
        ReclaimRetentionStateAtomicWriteTempsUnderWorkspaceOwnership();
        if (!File.Exists(_paths.CustomLoopReceiptProofLedgerPath))
        {
            return null;
        }

        var bytes = await _pathGuard.ReadAllBytesAsync(_paths.CustomLoopReceiptRetentionPath, _paths.CustomLoopReceiptProofLedgerPath, CustomLoopReceiptRetentionPolicy.MaxProofLedgerUtf8Bytes, "Custom loop receipt proof ledger", cancellationToken);
        RejectDuplicateProperties(bytes);
        try
        {
            return CustomLoopReceiptRetentionContractCodec.DeserializeProofLedger(bytes);
        }
        catch (ArgumentException exception)
        {
            throw new FormatException("Custom loop receipt proof ledger violates its schema-1 contract.", exception);
        }
    }

    private async Task<CustomLoopReceiptCleanupJournal?> ReadCleanupJournalAsync(CustomLoopReceiptArtifactClass artifactClass, CancellationToken cancellationToken)
    {
        ReclaimRetentionStateAtomicWriteTempsUnderWorkspaceOwnership();
        var path = GetCleanupJournalPath(artifactClass);
        if (!File.Exists(path))
        {
            return null;
        }

        var bytes = await _pathGuard.ReadAllBytesAsync(_paths.CustomLoopReceiptRetentionPath, path, CustomLoopReceiptRetentionPolicy.MaxCleanupJournalUtf8Bytes, "Custom loop receipt cleanup journal", cancellationToken);
        RejectDuplicateProperties(bytes);
        CustomLoopReceiptCleanupJournal journal;
        try
        {
            journal = CustomLoopReceiptRetentionContractCodec.DeserializeCleanupJournal(bytes);
        }
        catch (ArgumentException exception)
        {
            throw new FormatException("Custom loop receipt cleanup journal violates its schema-1 contract.", exception);
        }
        if (journal.Request.ArtifactClass != artifactClass)
        {
            throw new FormatException("Cleanup journal artifact class does not match its canonical path.");
        }

        return journal;
    }

    private string GetCleanupHistoryRoot(CustomLoopReceiptArtifactClass artifactClass)
    {
        return artifactClass switch
        {
            CustomLoopReceiptArtifactClass.DefinitionMutationReceipt => _paths.CustomLoopDefinitionMutationReceiptCleanupHistoryPath,
            CustomLoopReceiptArtifactClass.DefinitionTombstone => _paths.CustomLoopDefinitionTombstoneCleanupHistoryPath,
            _ => throw new ArgumentOutOfRangeException(nameof(artifactClass), artifactClass, "Authoring cleanup history requires a definition receipt or tombstone class.")
        };
    }

    private async Task WriteCleanupJournalAsync(CustomLoopReceiptCleanupJournal journal, CancellationToken cancellationToken)
    {
        var bytes = CustomLoopReceiptRetentionContractCodec.SerializeCleanupJournal(journal);
        await _pathGuard.WriteTextAtomicallyAsync(_paths.CustomLoopReceiptRetentionPath, GetCleanupJournalPath(journal.Request.ArtifactClass), Encoding.UTF8.GetString(bytes), cancellationToken);
    }

    private async Task<CustomLoopReceiptCleanupJournal> WriteJournalStageAsync(CustomLoopReceiptCleanupJournal journal, CustomLoopReceiptCleanupStage stage, CustomLoopReceiptCleanupOutcome outcome, string detail, CancellationToken cancellationToken)
    {
        var updated = UtcNow(journal.UpdatedAtUtc);
        if (updated - journal.OwnershipAcquiredAtUtc > CustomLoopReceiptRetentionPolicy.CleanupOwnershipWindow)
        {
            throw new IOException("Cleanup ownership expired before the durable stage transition completed.");
        }

        journal = journal with { Stage = stage, Outcome = outcome, UpdatedAtUtc = updated, Detail = detail };
        await WriteCleanupJournalAsync(journal, cancellationToken);
        return journal;
    }

    private async Task<IReadOnlyList<CustomLoopDefinitionRetentionArtifact>> ReadRetentionArtifactsAsync(CustomLoopReceiptArtifactClass artifactClass, CancellationToken cancellationToken)
    {
        var root = GetArtifactRoot(artifactClass);
        if (!_pathGuard.DirectoryExists(root))
        {
            return [];
        }

        var budget = CustomLoopReceiptRetentionPolicy.GetBudget(artifactClass);
        ReclaimRetentionAtomicWriteTempsUnderWorkspaceOwnership(root, artifactClass == CustomLoopReceiptArtifactClass.DefinitionMutationReceipt ? CustomLoopLimits.MaxMutationOperationIdCharacters : CustomLoopLimits.MaxArtifactIdCharacters, budget.MaximumArtifactCount, $"{artifactClass} storage");
        var paths = Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly).Take(budget.MaximumArtifactCount + 1).ToArray();
        if (paths.Length > budget.MaximumArtifactCount)
        {
            throw new FormatException($"{artifactClass} exceeds its bounded artifact-count ceiling.");
        }

        var result = new List<CustomLoopDefinitionRetentionArtifact>(paths.Length);
        long bytes = 0;
        foreach (var path in paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var artifact = await ReadRetentionArtifactAsync(artifactClass, path, cancellationToken);
            bytes = checked(bytes + artifact.Utf8Json.LongLength);
            if (bytes > budget.MaximumArtifactUtf8Bytes)
            {
                throw new FormatException($"{artifactClass} exceeds its bounded aggregate UTF-8 byte ceiling.");
            }

            result.Add(artifact);
        }

        if (result.Select(item => item.ArtifactId).Distinct(StringComparer.Ordinal).Count() != result.Count)
        {
            throw new FormatException($"{artifactClass} contains duplicate canonical identities.");
        }

        return result;
    }

    private async Task<(bool IsCanonical, bool HasConflict, int AttributedRemovedCount, long AttributedRemovedBytes)> ReconcileRemovalProgressAsync(CustomLoopReceiptCleanupJournal journal, CancellationToken cancellationToken)
    {
        var candidates = journal.Candidates.OrderBy(item => item.ArtifactId, StringComparer.Ordinal).ToArray();
        var root = GetArtifactRoot(journal.Request.ArtifactClass);
        if (!_pathGuard.DirectoryExists(root))
        {
            throw new DirectoryNotFoundException("Authoring receipt storage disappeared while reconstructing cleanup removal progress.");
        }

        var missingPrefixCount = 0;
        var retainedCandidateSeen = false;
        var canonical = true;
        var hasConflict = false;
        for (var index = 0; index < candidates.Length; index++)
        {
            var candidate = candidates[index];
            var path = GetCandidatePath(journal.Request.ArtifactClass, candidate.ArtifactId);
            var pendingPath = GetCandidatePendingRemovalPath(journal, candidate);
            var artifact = await ReadRetentionArtifactIfPresentAsync(journal.Request.ArtifactClass, path, candidate.ArtifactId, cancellationToken);
            var pendingArtifact = await ReadRetentionArtifactIfPresentAsync(journal.Request.ArtifactClass, pendingPath, candidate.ArtifactId, cancellationToken);
            if (pendingArtifact is not null)
            {
                if (artifact is not null || index != journal.RemovedArtifactCount || !CandidateMatches(pendingArtifact, candidate))
                {
                    canonical = false;
                    hasConflict = true;
                    break;
                }

                retainedCandidateSeen = true;
                continue;
            }

            if (artifact is null)
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
            if (!CandidateMatches(artifact, candidate))
            {
                canonical = false;
                hasConflict = true;
                break;
            }
        }

        if (missingPrefixCount > journal.RemovedArtifactCount + 1)
        {
            canonical = false;
        }

        var attributedCount = canonical ? Math.Max(journal.RemovedArtifactCount, missingPrefixCount) : journal.RemovedArtifactCount;
        var attributedBytes = candidates.Take(attributedCount).Sum(item => item.ArtifactUtf8Bytes);
        if (journal.RemovedArtifactCount > missingPrefixCount)
        {
            canonical = false;
        }

        return (canonical, hasConflict, attributedCount, attributedBytes);
    }

    private async Task<bool> RemoveCandidateWithVerifiedTransitionAsync(CustomLoopReceiptCleanupJournal journal, CustomLoopReceiptCleanupCandidate candidate, CancellationToken cancellationToken)
    {
        var artifactClass = journal.Request.ArtifactClass;
        var root = GetArtifactRoot(artifactClass);
        if (!_pathGuard.DirectoryExists(root))
        {
            throw new DirectoryNotFoundException("Authoring receipt storage disappeared during cleanup removal.");
        }

        var sourcePath = GetCandidatePath(artifactClass, candidate.ArtifactId);
        var pendingPath = GetCandidatePendingRemovalPath(journal, candidate);
        var source = await ReadRetentionArtifactIfPresentAsync(artifactClass, sourcePath, candidate.ArtifactId, cancellationToken);
        var pending = await ReadRetentionArtifactIfPresentAsync(artifactClass, pendingPath, candidate.ArtifactId, cancellationToken);
        if (source is not null && pending is not null)
        {
            return false;
        }

        if (pending is null)
        {
            if (source is null || !CandidateMatches(source, candidate))
            {
                return false;
            }

            _pathGuard.MoveFileIfDestinationAbsent(root, sourcePath, pendingPath);
            pending = await ReadRetentionArtifactAsync(artifactClass, pendingPath, cancellationToken, candidate.ArtifactId);
        }

        if (!CandidateMatches(pending, candidate) || await ReadRetentionArtifactIfPresentAsync(artifactClass, sourcePath, candidate.ArtifactId, cancellationToken) is not null)
        {
            return false;
        }

        _pathGuard.DeleteFile(root, pendingPath);
        return await ReadRetentionArtifactIfPresentAsync(artifactClass, sourcePath, candidate.ArtifactId, cancellationToken) is null
            && await ReadRetentionArtifactIfPresentAsync(artifactClass, pendingPath, candidate.ArtifactId, cancellationToken) is null;
    }

    private async Task<CustomLoopDefinitionRetentionArtifact?> ReadRetentionArtifactIfPresentAsync(CustomLoopReceiptArtifactClass artifactClass, string path, string expectedArtifactId, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadRetentionArtifactAsync(artifactClass, path, cancellationToken, expectedArtifactId);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private string GetCandidatePendingRemovalPath(CustomLoopReceiptCleanupJournal journal, CustomLoopReceiptCleanupCandidate candidate)
    {
        var identity = Encoding.UTF8.GetBytes($"{journal.Request.ArtifactClass}\n{journal.Request.OperationId}\n{candidate.ArtifactId}");
        var hash = Convert.ToHexString(SHA256.HashData(identity)).ToLowerInvariant();
        return _pathGuard.GetFilePath(GetArtifactRoot(journal.Request.ArtifactClass), $".retention-removal-{hash}.pending");
    }

    private static bool CandidateMatches(CustomLoopDefinitionRetentionArtifact artifact, CustomLoopReceiptCleanupCandidate candidate)
    {
        return string.Equals(artifact.Hash, candidate.ArtifactHash, StringComparison.Ordinal) && artifact.Utf8Json.LongLength == candidate.ArtifactUtf8Bytes;
    }

    private async Task<CustomLoopDefinitionRetentionArtifact> ReadRetentionArtifactAsync(CustomLoopReceiptArtifactClass artifactClass, string path, CancellationToken cancellationToken, string? expectedArtifactId = null)
    {
        var root = GetArtifactRoot(artifactClass);
        var maximum = artifactClass == CustomLoopReceiptArtifactClass.DefinitionMutationReceipt ? MaxDefinitionMutationOperationArtifactBytes : MaxTombstoneArtifactBytes;
        var bytes = await _pathGuard.ReadAllBytesAsync(root, path, maximum, artifactClass.ToString(), cancellationToken);
        RejectDuplicateProperties(bytes);
        try
        {
            if (artifactClass == CustomLoopReceiptArtifactClass.DefinitionMutationReceipt)
            {
                var operation = JsonSerializer.Deserialize<CustomLoopDefinitionMutationOperationRecord>(bytes, _jsonOptions) ?? throw new FormatException("Definition mutation receipt is empty.");
                ValidateMutationOperation(operation);
                if (!string.Equals(operation.OperationId, expectedArtifactId ?? Path.GetFileNameWithoutExtension(path), StringComparison.Ordinal))
                {
                    throw new FormatException("Definition mutation receipt identity does not match its filename.");
                }

                return new(operation.OperationId, path, bytes, Hash(bytes), operation, null);
            }

            var tombstone = JsonSerializer.Deserialize<CustomLoopDefinitionTombstone>(bytes, _jsonOptions) ?? throw new FormatException("Definition tombstone is empty.");
            ValidateTombstone(tombstone);
            if (!string.Equals(tombstone.LoopId, expectedArtifactId ?? Path.GetFileNameWithoutExtension(path), StringComparison.Ordinal))
            {
                throw new FormatException("Definition tombstone identity does not match its filename.");
            }

            return new(tombstone.LoopId, path, bytes, Hash(bytes), null, tombstone);
        }
        catch (JsonException exception)
        {
            throw new FormatException($"{artifactClass} contains noncanonical JSON.", exception);
        }
    }

    private async Task<bool> TryAppendCleanupAuditAsync(CustomLoopReceiptCleanupJournal journal, bool intent, string outcome, CancellationToken cancellationToken, CancellationToken callerCancellationToken)
    {
        try
        {
            await _auditLog.AppendAsync(new AuditEvent(
                UtcNow(journal.UpdatedAtUtc),
                journal.Request.Actor,
                intent ? AuditSchema.Actions.LoopDefinitionReceiptRetentionIntent : AuditSchema.Actions.LoopDefinitionReceiptRetentionOutcome,
                journal.Request.OperationId,
                outcome,
                intent ? "Governed bounded authoring-receipt cleanup intent." : "Governed bounded authoring-receipt cleanup outcome.",
                new Dictionary<string, object?>
                {
                    ["artifactClass"] = journal.Request.ArtifactClass.ToString(),
                    ["surface"] = journal.Request.Surface,
                    ["candidateCount"] = journal.Candidates.Length,
                    ["candidateUtf8Bytes"] = journal.Candidates.Sum(item => item.ArtifactUtf8Bytes),
                    ["ownerGenerationId"] = journal.OwnerGenerationId
                }), cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !callerCancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private DateTimeOffset UtcNow(DateTimeOffset minimum)
    {
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        return now < minimum ? minimum : now;
    }

    private static CustomLoopExpiredOperationProof ToExpiredProof(CustomLoopDefinitionMutationOperationRecord operation, string outcomeHash, CustomLoopDefinitionLineageProof? lineage)
    {
        var successfulDelete = operation.Kind == CustomLoopDefinitionMutationKind.Delete && operation.Outcome == CustomLoopDefinitionStoreStatus.Deleted;
        var deleteBindingHash = successfulDelete
            ? CustomLoopReceiptRetentionContractCodec.ComputeDeleteLineageBindingHash(operation.RequestHash, outcomeHash, lineage ?? throw new FormatException($"Delete receipt `{operation.OperationId}` is missing its canonical lineage proof."))
            : null;
        if (!successfulDelete && lineage is not null)
        {
            throw new FormatException($"Non-deleting receipt `{operation.OperationId}` cannot own deleted lineage proof.");
        }

        return new CustomLoopExpiredOperationProof(
            CustomLoopExpiredOperationProof.CurrentSchemaVersion,
            CustomLoopReceiptArtifactClass.DefinitionMutationReceipt,
            operation.Kind,
            operation.Outcome,
            deleteBindingHash,
            operation.OperationId,
            operation.RequestHash,
            outcomeHash,
            operation.UpdatedAtUtc,
            operation.UpdatedAtUtc + CustomLoopReceiptRetentionPolicy.ExactReplayDuration);
    }

    private static CustomLoopExpiredOperationProof ToAdmissionExpiredProof(CustomLoopDefinitionMutationRequest mutation)
    {
        var completedAtUtc = GetMaximumAdmissionProofTimestampUtc();
        var outcome = mutation.Kind == CustomLoopDefinitionMutationKind.Delete && mutation.PriorDefinition is null
            ? CustomLoopDefinitionStoreStatus.NotFound
            : MaximumAdmissionOutcome(mutation.Kind);
        return new CustomLoopExpiredOperationProof(
            CustomLoopExpiredOperationProof.CurrentSchemaVersion,
            CustomLoopReceiptArtifactClass.DefinitionMutationReceipt,
            mutation.Kind,
            outcome,
            outcome == CustomLoopDefinitionStoreStatus.Deleted ? new string('0', CustomLoopLimits.Sha256HexCharacters) : null,
            mutation.OperationId,
            mutation.RequestHash,
            new string('0', CustomLoopLimits.Sha256HexCharacters),
            completedAtUtc,
            completedAtUtc + CustomLoopReceiptRetentionPolicy.ExactReplayDuration);
    }

    private static CustomLoopExpiredOperationProof ToOutstandingExpiredProof(CustomLoopDefinitionRetentionArtifact artifact)
    {
        var operation = artifact.Operation!;
        var completedAtUtc = operation.State == CustomLoopDefinitionMutationState.OutcomeCommitted ? operation.UpdatedAtUtc : GetMaximumAdmissionProofTimestampUtc();
        var outcome = operation.State == CustomLoopDefinitionMutationState.OutcomeCommitted ? operation.Outcome : MaximumAdmissionOutcome(operation.Kind);
        return new CustomLoopExpiredOperationProof(
            CustomLoopExpiredOperationProof.CurrentSchemaVersion,
            CustomLoopReceiptArtifactClass.DefinitionMutationReceipt,
            operation.Kind,
            outcome,
            outcome == CustomLoopDefinitionStoreStatus.Deleted ? new string('0', CustomLoopLimits.Sha256HexCharacters) : null,
            operation.OperationId,
            operation.RequestHash,
            operation.State == CustomLoopDefinitionMutationState.OutcomeCommitted ? artifact.Hash : new string('0', CustomLoopLimits.Sha256HexCharacters),
            completedAtUtc,
            completedAtUtc + CustomLoopReceiptRetentionPolicy.ExactReplayDuration);
    }

    private static CustomLoopDefinitionStoreStatus MaximumAdmissionOutcome(CustomLoopDefinitionMutationKind kind)
    {
        return kind switch
        {
            CustomLoopDefinitionMutationKind.Create => CustomLoopDefinitionStoreStatus.LimitExceeded,
            CustomLoopDefinitionMutationKind.Update => CustomLoopDefinitionStoreStatus.NotFound,
            CustomLoopDefinitionMutationKind.Delete => CustomLoopDefinitionStoreStatus.Deleted,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Mutation kind has no valid retention-proof outcome.")
        };
    }

    private static CustomLoopDefinitionLineageProof ToAdmissionLineageProof(CustomLoopDefinitionMutationRequest mutation)
    {
        var prior = mutation.PriorDefinition ?? throw new ArgumentException("A delete admission must retain its prior definition snapshot.", nameof(mutation));
        return new CustomLoopDefinitionLineageProof(
            CustomLoopDefinitionLineageProof.CurrentSchemaVersion,
            prior.Id,
            prior.RoleId,
            prior.DefinitionVersion,
            prior.ContentHash,
            mutation.OperationId,
            IsDeleted: true,
            GetMaximumAdmissionProofTimestampUtc());
    }

    private static DateTimeOffset GetMaximumAdmissionProofTimestampUtc() => new DateTimeOffset(9999, 11, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(9_999_999);

    private static CustomLoopDefinitionLineageProof ToLineageProof(CustomLoopDefinitionMutationOperationRecord operation, CustomLoopDefinitionTombstone tombstone)
    {
        if (!DeleteOperationMatchesTombstone(operation, tombstone))
        {
            throw new FormatException($"Delete receipt `{operation.OperationId}` does not bind its loop, version, prior/result hashes, tombstone result, and deletion timestamp to tombstone `{tombstone.LoopId}`.");
        }

        var definition = operation.PriorDefinition!;
        return new CustomLoopDefinitionLineageProof(
            CustomLoopDefinitionLineageProof.CurrentSchemaVersion,
            tombstone.LoopId,
            definition.RoleId,
            tombstone.LastDefinitionVersion,
            tombstone.LastContentHash,
            tombstone.MutationOperationId,
            IsDeleted: true,
            tombstone.DeletedAtUtc);
    }

    private static bool DeleteOperationMatchesTombstone(CustomLoopDefinitionMutationOperationRecord operation, CustomLoopDefinitionTombstone tombstone)
    {
        var prior = operation.PriorDefinition;
        var result = operation.ResultDefinition;
        return operation.Kind == CustomLoopDefinitionMutationKind.Delete
            && operation.State == CustomLoopDefinitionMutationState.OutcomeCommitted
            && operation.Outcome == CustomLoopDefinitionStoreStatus.Deleted
            && prior is not null
            && result is not null
            && DefinitionSnapshotsEqual(result, prior)
            && string.Equals(result.RoleId, prior.RoleId, StringComparison.Ordinal)
            && result.CreatedAtUtc == prior.CreatedAtUtc
            && operation.ResultTombstone == tombstone
            && string.Equals(operation.LoopId, tombstone.LoopId, StringComparison.Ordinal)
            && string.Equals(operation.RoleId, prior.RoleId, StringComparison.Ordinal)
            && operation.ExpectedDefinitionVersion == tombstone.LastDefinitionVersion
            && string.Equals(prior.Id, tombstone.LoopId, StringComparison.Ordinal)
            && prior.DefinitionVersion == tombstone.LastDefinitionVersion
            && string.Equals(prior.ContentHash, tombstone.LastContentHash, StringComparison.Ordinal)
            && string.Equals(operation.OperationId, tombstone.MutationOperationId, StringComparison.Ordinal)
            && operation.UpdatedAtUtc == tombstone.DeletedAtUtc;
    }

    private static bool TryAddCandidate(CustomLoopReceiptCleanupRequest request, List<CustomLoopReceiptCleanupCandidate> candidates, ref long bytes, CustomLoopReceiptCleanupCandidate candidate)
    {
        if (candidates.Count >= request.MaximumArtifactCount || candidate.ArtifactUtf8Bytes > request.MaximumArtifactUtf8Bytes - bytes)
        {
            return false;
        }

        candidates.Add(candidate);
        bytes += candidate.ArtifactUtf8Bytes;
        return true;
    }

    private static bool LineageMatchesTombstone(CustomLoopDefinitionLineageProof lineage, CustomLoopDefinitionTombstone tombstone)
    {
        return lineage.IsDeleted
            && string.Equals(lineage.LoopId, tombstone.LoopId, StringComparison.Ordinal)
            && lineage.LastDefinitionVersion == tombstone.LastDefinitionVersion
            && string.Equals(lineage.LastDefinitionHash, tombstone.LastContentHash, StringComparison.Ordinal)
            && string.Equals(lineage.LastMutationOperationId, tombstone.MutationOperationId, StringComparison.Ordinal)
            && lineage.DeletedAtUtc == tombstone.DeletedAtUtc;
    }

    private static CustomLoopReceiptArtifactCategory ClassifyOperation(CustomLoopDefinitionRetentionArtifact artifact, CustomLoopReceiptProofLedger? ledger, IReadOnlySet<string>? liveLoopIds, DateTimeOffset observedAtUtc, List<DateTimeOffset> liveExpiries)
    {
        var operation = artifact.Operation!;
        var compactProof = ledger?.ExpiredOperations.SingleOrDefault(item => item.ArtifactClass == CustomLoopReceiptArtifactClass.DefinitionMutationReceipt && string.Equals(item.OperationId, operation.OperationId, StringComparison.Ordinal));
        var retainedLineage = ledger?.DefinitionLineage.SingleOrDefault(item => string.Equals(item.LastMutationOperationId, operation.OperationId, StringComparison.Ordinal));
        if (compactProof is not null && !ProofMatchesOperation(compactProof, artifact, retainedLineage))
        {
            return CustomLoopReceiptArtifactCategory.Ambiguous;
        }

        if (operation.State == CustomLoopDefinitionMutationState.PendingMutation)
        {
            return CustomLoopReceiptArtifactCategory.Pending;
        }

        if (!operation.OutcomeAuditRecorded)
        {
            return CustomLoopReceiptArtifactCategory.Unaudited;
        }

        var expires = operation.UpdatedAtUtc + CustomLoopReceiptRetentionPolicy.ExactReplayDuration;
        if (expires > observedAtUtc)
        {
            liveExpiries.Add(expires);
            return CustomLoopReceiptArtifactCategory.Live;
        }

        if (operation.Kind == CustomLoopDefinitionMutationKind.Create)
        {
            if (liveLoopIds?.Contains(operation.LoopId) == true)
            {
                return CustomLoopReceiptArtifactCategory.RetainedLiveLineage;
            }

            if (ledger?.DefinitionLineage.Any(item => string.Equals(item.LoopId, operation.LoopId, StringComparison.Ordinal)) != true)
            {
                return CustomLoopReceiptArtifactCategory.Degraded;
            }
        }

        return CustomLoopReceiptArtifactCategory.Compactable;
    }

    private static CustomLoopReceiptArtifactCategory ClassifyTombstone(CustomLoopDefinitionTombstone tombstone, IReadOnlyDictionary<string, CustomLoopDefinitionRetentionArtifact> operations, CustomLoopReceiptProofLedger? ledger, DateTimeOffset observedAtUtc, List<DateTimeOffset> liveExpiries)
    {
        if (operations.TryGetValue(tombstone.MutationOperationId, out var artifact))
        {
            if (!DeleteOperationMatchesTombstone(artifact.Operation!, tombstone))
            {
                return CustomLoopReceiptArtifactCategory.Corrupt;
            }

            var retainedLineage = ledger?.DefinitionLineage.SingleOrDefault(item => string.Equals(item.LoopId, tombstone.LoopId, StringComparison.Ordinal));
            if (retainedLineage is not null && !LineageMatchesTombstone(retainedLineage, tombstone))
            {
                return CustomLoopReceiptArtifactCategory.Ambiguous;
            }

            return ClassifyOperation(artifact, ledger, liveLoopIds: null, observedAtUtc, liveExpiries);
        }

        var proof = ledger?.ExpiredOperations.SingleOrDefault(item => item.ArtifactClass == CustomLoopReceiptArtifactClass.DefinitionMutationReceipt && string.Equals(item.OperationId, tombstone.MutationOperationId, StringComparison.Ordinal));
        var lineage = ledger?.DefinitionLineage.SingleOrDefault(item => string.Equals(item.LoopId, tombstone.LoopId, StringComparison.Ordinal));
        return proof is not null && lineage is not null && LineageMatchesTombstone(lineage, tombstone)
            ? CustomLoopReceiptArtifactCategory.Compactable
            : CustomLoopReceiptArtifactCategory.Degraded;
    }

    private static void AddUsage(Dictionary<CustomLoopReceiptArtifactCategory, (int Count, long Bytes)> usage, CustomLoopReceiptArtifactCategory category, long bytes)
    {
        var current = usage[category];
        usage[category] = (current.Count + 1, checked(current.Bytes + bytes));
    }

    private void ReclaimRetentionAtomicWriteTempsUnderWorkspaceOwnership(string root, int maximumIdentifierLength, int maximumArtifactCount, string artifactName)
    {
        if (Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly).Take(1).Any())
        {
            throw new FormatException($"{artifactName} cannot contain subdirectories.");
        }

        var boundedPaths = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly).Take(maximumArtifactCount + 2).ToArray();
        if (boundedPaths.Length > maximumArtifactCount + 1)
        {
            throw new FormatException($"{artifactName} exceeds its bounded inventory ceiling.");
        }

        foreach (var path in boundedPaths.OrderBy(path => path, StringComparer.Ordinal))
        {
            var fileName = Path.GetFileName(path);
            if (IsAtomicWriteTemp(fileName, target => IsRetentionArtifactFileName(target, maximumIdentifierLength)))
            {
                _pathGuard.DeleteFile(root, path);
                continue;
            }

            if (!IsRetentionArtifactFileName(fileName, maximumIdentifierLength))
            {
                throw new FormatException($"{artifactName} contains an unrecognized artifact `{fileName}`.");
            }
        }
    }

    private void ReclaimRetentionStateAtomicWriteTempsUnderWorkspaceOwnership()
    {
        if (!_pathGuard.DirectoryExists(_paths.CustomLoopReceiptRetentionPath))
        {
            return;
        }

        var canonicalFiles = new HashSet<string>(StringComparer.Ordinal)
        {
            Path.GetFileName(_paths.CustomLoopReceiptProofLedgerPath),
            Path.GetFileName(_paths.CustomLoopDefinitionMutationReceiptCleanupJournalPath),
            Path.GetFileName(_paths.CustomLoopDefinitionTombstoneCleanupJournalPath)
        };
        var lifecycleCleanupDirectory = Path.GetFileName(_paths.CustomLoopControlReceiptCleanupPath);
        if (Directory.EnumerateDirectories(_paths.CustomLoopReceiptRetentionPath, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Any(directoryName => !string.Equals(directoryName, lifecycleCleanupDirectory, StringComparison.Ordinal)))
        {
            throw new FormatException("Custom-loop receipt retention storage cannot contain subdirectories.");
        }

        const int AdditionalOwnedFileCount = 2; // The shared mutation lock plus at most one interrupted atomic write.
        var boundedPaths = Directory.EnumerateFiles(_paths.CustomLoopReceiptRetentionPath, "*", SearchOption.TopDirectoryOnly).Take(canonicalFiles.Count + AdditionalOwnedFileCount + 1).ToArray();
        if (boundedPaths.Length > canonicalFiles.Count + AdditionalOwnedFileCount)
        {
            throw new FormatException("Custom-loop receipt retention storage exceeds its bounded inventory ceiling.");
        }

        foreach (var path in boundedPaths.OrderBy(path => path, StringComparer.Ordinal))
        {
            var fileName = Path.GetFileName(path);
            if (string.Equals(fileName, ".custom-loop-mutations.lock", StringComparison.Ordinal))
            {
                // Lifecycle-control receipt retention shares this root and owns the same cross-process mutation lock.
                continue;
            }

            if (IsAtomicWriteTemp(fileName, canonicalFiles.Contains))
            {
                _pathGuard.DeleteFile(_paths.CustomLoopReceiptRetentionPath, path);
                continue;
            }

            if (!canonicalFiles.Contains(fileName))
            {
                throw new FormatException($"Custom-loop receipt retention storage contains an unrecognized artifact `{fileName}`.");
            }
        }
    }

    private static bool IsRetentionArtifactFileName(string fileName, int maximumIdentifierLength)
    {
        var identifier = Path.GetFileNameWithoutExtension(fileName);
        return string.Equals(Path.GetExtension(fileName), ".json", StringComparison.Ordinal)
            && CustomLoopArtifactIdentifier.IsValid(identifier, maximumIdentifierLength);
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

    private static bool ProofMatchesOperation(CustomLoopExpiredOperationProof proof, CustomLoopDefinitionRetentionArtifact artifact, CustomLoopDefinitionLineageProof? lineage)
    {
        var operation = artifact.Operation!;
        var successfulDelete = operation.Kind == CustomLoopDefinitionMutationKind.Delete && operation.Outcome == CustomLoopDefinitionStoreStatus.Deleted;
        var deleteBindingMatches = successfulDelete
            ? operation.ResultTombstone is { } tombstone
                && lineage is { } retainedLineage
                && retainedLineage == ToLineageProof(operation, tombstone)
                && string.Equals(proof.DeleteLineageBindingHash, CustomLoopReceiptRetentionContractCodec.ComputeDeleteLineageBindingHash(operation.RequestHash, artifact.Hash, retainedLineage), StringComparison.Ordinal)
            : proof.DeleteLineageBindingHash is null && lineage is null;
        return proof.DefinitionMutationKind == operation.Kind
            && proof.DefinitionMutationOutcome == operation.Outcome
            && deleteBindingMatches
            && string.Equals(proof.OperationId, operation.OperationId, StringComparison.Ordinal)
            && string.Equals(proof.RequestHash, operation.RequestHash, StringComparison.Ordinal)
            && string.Equals(proof.OutcomeHash, artifact.Hash, StringComparison.Ordinal)
            && proof.CompletedAtUtc == operation.UpdatedAtUtc
            && proof.ExpiredAtUtc == operation.UpdatedAtUtc + CustomLoopReceiptRetentionPolicy.ExactReplayDuration;
    }

    private static void AddProofUsage<T>(Dictionary<CustomLoopReceiptArtifactCategory, (int Count, long Bytes)> usage, CustomLoopReceiptArtifactCategory category, IEnumerable<T> proofs, Func<T, int> measure)
    {
        var proofUsage = GetProofUsage(proofs, measure);
        if (proofUsage.Count == 0)
        {
            return;
        }

        var current = usage[category];
        usage[category] = (current.Count + proofUsage.Count, checked(current.Bytes + proofUsage.Utf8Bytes));
    }

    private static (int Count, long Utf8Bytes) GetProofUsage<T>(IEnumerable<T> proofs, Func<T, int> measure)
    {
        var entries = proofs.ToArray();
        return entries.Length == 0
            ? (0, 0)
            : (entries.Length, checked(entries.Select(measure).Sum(value => (long)value) + entries.Length - 1));
    }

    private static CustomLoopReceiptCleanupBlockReason MergeBlockReason(CustomLoopReceiptCleanupBlockReason current, CustomLoopReceiptArtifactCategory category)
    {
        var candidate = category switch
        {
            CustomLoopReceiptArtifactCategory.Pending => CustomLoopReceiptCleanupBlockReason.PendingEvidence,
            CustomLoopReceiptArtifactCategory.Unaudited => CustomLoopReceiptCleanupBlockReason.UnauditedEvidence,
            CustomLoopReceiptArtifactCategory.Degraded => CustomLoopReceiptCleanupBlockReason.DegradedEvidence,
            CustomLoopReceiptArtifactCategory.Corrupt => CustomLoopReceiptCleanupBlockReason.CorruptEvidence,
            CustomLoopReceiptArtifactCategory.OwnershipUnresolved => CustomLoopReceiptCleanupBlockReason.OwnershipUnresolved,
            CustomLoopReceiptArtifactCategory.Ambiguous => CustomLoopReceiptCleanupBlockReason.AmbiguousEvidence,
            _ => CustomLoopReceiptCleanupBlockReason.None
        };
        return candidate > current ? candidate : current;
    }

    private static CustomLoopReceiptQuotaExhaustionReason GetExhaustionReason(CustomLoopReceiptRetentionBudget budget, int artifactCount, long artifactBytes, long maximumNewArtifactBytes, int proofCount, long proofBytes)
    {
        var artifactExhaustion = budget.GetArtifactExhaustionReason(artifactCount, artifactBytes, 1, maximumNewArtifactBytes, integrityPreservingCompletion: false);
        if (artifactExhaustion != CustomLoopReceiptQuotaExhaustionReason.None)
        {
            return artifactExhaustion;
        }

        if (proofCount >= budget.MaximumProofCount)
        {
            return CustomLoopReceiptQuotaExhaustionReason.ProofCountLimit;
        }

        return proofBytes >= budget.MaximumProofUtf8Bytes ? CustomLoopReceiptQuotaExhaustionReason.ProofByteLimit : CustomLoopReceiptQuotaExhaustionReason.None;
    }

    private static CustomLoopReceiptQuotaExhaustionReason GetProofExhaustion(ArgumentException exception)
    {
        return exception.Message.Contains("byte", StringComparison.OrdinalIgnoreCase)
            ? CustomLoopReceiptQuotaExhaustionReason.ProofByteLimit
            : CustomLoopReceiptQuotaExhaustionReason.ProofCountLimit;
    }

    private static CustomLoopReceiptCleanupResult MapTerminalJournal(CustomLoopReceiptCleanupJournal journal, bool replay)
    {
        return journal.Stage switch
        {
            CustomLoopReceiptCleanupStage.Completed when journal.Outcome == CustomLoopReceiptCleanupOutcome.NothingEligible => CleanupResult(CustomLoopReceiptCleanupStatus.NothingEligible, journal, detail: journal.Detail, isReplay: replay),
            CustomLoopReceiptCleanupStage.Completed => CleanupResult(replay ? CustomLoopReceiptCleanupStatus.Replayed : CustomLoopReceiptCleanupStatus.Pruned, journal, compactedCount: journal.RemovedArtifactCount, compactedBytes: journal.RemovedArtifactUtf8Bytes, detail: journal.Detail, isReplay: replay),
            CustomLoopReceiptCleanupStage.CommittedWithAuditWarning => CleanupResult(CustomLoopReceiptCleanupStatus.CommittedWithAuditWarning, journal, blockReason: CustomLoopReceiptCleanupBlockReason.AuditUnavailable, compactedCount: journal.RemovedArtifactCount, compactedBytes: journal.RemovedArtifactUtf8Bytes, detail: journal.Detail, isReplay: replay),
            CustomLoopReceiptCleanupStage.AbandonedConflict => CleanupResult(CustomLoopReceiptCleanupStatus.CleanupConflict, journal, blockReason: CustomLoopReceiptCleanupBlockReason.CleanupConflict, detail: journal.Detail, isReplay: replay),
            CustomLoopReceiptCleanupStage.Degraded when journal.Outcome == CustomLoopReceiptCleanupOutcome.AuditUnavailable => CleanupResult(CustomLoopReceiptCleanupStatus.AuditUnavailable, journal, blockReason: CustomLoopReceiptCleanupBlockReason.AuditUnavailable, detail: journal.Detail, isReplay: replay),
            CustomLoopReceiptCleanupStage.Degraded when journal.Outcome == CustomLoopReceiptCleanupOutcome.Corrupt => CleanupResult(CustomLoopReceiptCleanupStatus.Corrupt, journal, blockReason: CustomLoopReceiptCleanupBlockReason.CorruptEvidence, detail: journal.Detail, isReplay: replay),
            _ => CleanupResult(CustomLoopReceiptCleanupStatus.Degraded, journal, blockReason: CustomLoopReceiptCleanupBlockReason.AmbiguousEvidence, detail: journal.Detail, isReplay: replay)
        };
    }

    private static CustomLoopReceiptCleanupResult CleanupResult(CustomLoopReceiptCleanupStatus status, CustomLoopReceiptCleanupJournal? journal, CustomLoopReceiptQuotaExhaustionReason exhaustionReason = CustomLoopReceiptQuotaExhaustionReason.None, CustomLoopReceiptCleanupBlockReason blockReason = CustomLoopReceiptCleanupBlockReason.None, int compactedCount = 0, long compactedBytes = 0, string detail = "Receipt cleanup stopped safely.", bool isReplay = false)
    {
        return new CustomLoopReceiptCleanupResult(status, journal, exhaustionReason, blockReason, compactedCount, compactedBytes, detail) { IsReplay = isReplay };
    }

    private string GetArtifactRoot(CustomLoopReceiptArtifactClass artifactClass) => artifactClass switch
    {
        CustomLoopReceiptArtifactClass.DefinitionMutationReceipt => _paths.CustomLoopDefinitionOperationsPath,
        CustomLoopReceiptArtifactClass.DefinitionTombstone => _paths.CustomLoopDefinitionTombstonesPath,
        _ => throw new ArgumentOutOfRangeException(nameof(artifactClass))
    };

    private string GetCandidatePath(CustomLoopReceiptArtifactClass artifactClass, string artifactId) => artifactClass switch
    {
        CustomLoopReceiptArtifactClass.DefinitionMutationReceipt => GetOperationPath(artifactId),
        CustomLoopReceiptArtifactClass.DefinitionTombstone => GetTombstonePath(artifactId),
        _ => throw new ArgumentOutOfRangeException(nameof(artifactClass))
    };

    private string GetCleanupJournalPath(CustomLoopReceiptArtifactClass artifactClass) => artifactClass switch
    {
        CustomLoopReceiptArtifactClass.DefinitionMutationReceipt => _paths.CustomLoopDefinitionMutationReceiptCleanupJournalPath,
        CustomLoopReceiptArtifactClass.DefinitionTombstone => _paths.CustomLoopDefinitionTombstoneCleanupJournalPath,
        _ => throw new ArgumentOutOfRangeException(nameof(artifactClass))
    };

    private static bool IsTerminal(CustomLoopReceiptCleanupStage stage) => stage is CustomLoopReceiptCleanupStage.Completed or CustomLoopReceiptCleanupStage.CommittedWithAuditWarning or CustomLoopReceiptCleanupStage.AbandonedConflict or CustomLoopReceiptCleanupStage.Degraded;

    private static void RequireAuthoringArtifactClass(CustomLoopReceiptArtifactClass artifactClass)
    {
        if (artifactClass is not CustomLoopReceiptArtifactClass.DefinitionMutationReceipt and not CustomLoopReceiptArtifactClass.DefinitionTombstone)
        {
            throw new ArgumentOutOfRangeException(nameof(artifactClass), artifactClass, "The definition store owns only authoring receipts and tombstones.");
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
