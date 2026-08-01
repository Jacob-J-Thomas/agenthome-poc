using System.Text;
using EmbodySense.Core.Application.Loops.ReceiptRetention.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Retention;
using EmbodySense.Core.Common.Loops.Models.Custom.Retention;

namespace EmbodySense.Core.Application.Loops.ReceiptRetention;

/// <summary>
/// Validates schema-1 receipt-retention contracts before persistence, cleanup, or projection.
/// </summary>
public static class CustomLoopReceiptRetentionContractValidator
{
    /// <summary>
    /// Validates that a budget is the canonical immutable policy budget for its class.
    /// </summary>
    /// <param name="budget">The budget to validate.</param>
    public static void ValidateBudget(CustomLoopReceiptRetentionBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        var expected = CustomLoopReceiptRetentionPolicy.GetBudget(budget.ArtifactClass);
        if (budget != expected
            || budget.ReservedPendingCompletionCount < 1
            || budget.ReservedPendingCompletionCount >= budget.MaximumArtifactCount
            || budget.ReservedPendingCompletionUtf8Bytes < 1
            || budget.ReservedPendingCompletionUtf8Bytes >= budget.MaximumArtifactUtf8Bytes
            || budget.MaximumProofCount < 1
            || budget.MaximumProofUtf8Bytes < 1)
        {
            throw new ArgumentException("Receipt retention budget does not match the canonical class policy.", nameof(budget));
        }
    }

    /// <summary>
    /// Validates an explicitly bounded governed cleanup request.
    /// </summary>
    /// <param name="request">The request to validate.</param>
    public static void ValidateCleanupRequest(CustomLoopReceiptCleanupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireSchema(request.SchemaVersion, CustomLoopReceiptCleanupRequest.CurrentSchemaVersion, "cleanup request");
        _ = CustomLoopReceiptRetentionPolicy.GetBudget(request.ArtifactClass);
        RequireIdentifier(request.OperationId, nameof(request.OperationId));
        RequireBoundedText(request.Actor, CustomLoopLimits.MaxTraceReferenceCharacters, nameof(request.Actor));
        RequireIdentifier(request.Surface, nameof(request.Surface));
        RequireUtc(request.RequestedAtUtc, nameof(request.RequestedAtUtc));
        RequireUtc(request.ReplayCutoffUtc, nameof(request.ReplayCutoffUtc));
        if (request.ReplayCutoffUtc != CustomLoopReceiptRetentionPolicy.GetReplayCutoffUtc(request.RequestedAtUtc))
        {
            throw new ArgumentException("Cleanup replay cutoff must equal the inclusive exact-replay cutoff for the request timestamp.", nameof(request));
        }

        if (request.MaximumArtifactCount is < 1 or > CustomLoopReceiptRetentionPolicy.MaxCleanupBatchArtifactCount
            || request.MaximumArtifactUtf8Bytes is < 1 or > CustomLoopReceiptRetentionPolicy.MaxCleanupBatchArtifactUtf8Bytes)
        {
            throw new ArgumentException("Cleanup batch bounds exceed the governed per-operation limits.", nameof(request));
        }
    }

    /// <summary>
    /// Validates compact proof that an operation identity expired.
    /// </summary>
    /// <param name="proof">The expired-operation proof.</param>
    public static void ValidateExpiredOperationProof(CustomLoopExpiredOperationProof proof)
    {
        ArgumentNullException.ThrowIfNull(proof);
        RequireSchema(proof.SchemaVersion, CustomLoopExpiredOperationProof.CurrentSchemaVersion, "expired-operation proof");
        if (proof.ArtifactClass is not CustomLoopReceiptArtifactClass.DefinitionMutationReceipt and not CustomLoopReceiptArtifactClass.LifecycleControlReceipt)
        {
            throw new ArgumentException("Expired-operation proof must identify a receipt class with an idempotent request.", nameof(proof));
        }

        RequireIdentifier(proof.OperationId, nameof(proof.OperationId));
        RequireHash(proof.RequestHash, nameof(proof.RequestHash));
        RequireHash(proof.OutcomeHash, nameof(proof.OutcomeHash));
        RequireUtc(proof.CompletedAtUtc, nameof(proof.CompletedAtUtc));
        RequireUtc(proof.ExpiredAtUtc, nameof(proof.ExpiredAtUtc));
        if (proof.ExpiredAtUtc != proof.CompletedAtUtc + CustomLoopReceiptRetentionPolicy.ExactReplayDuration)
        {
            throw new ArgumentException("Expired-operation proof must retain the exact documented replay expiry timestamp.", nameof(proof));
        }
    }

    /// <summary>
    /// Validates compact definition lineage and loop-identity non-reuse proof.
    /// </summary>
    /// <param name="proof">The lineage proof.</param>
    public static void ValidateDefinitionLineageProof(CustomLoopDefinitionLineageProof proof)
    {
        ArgumentNullException.ThrowIfNull(proof);
        RequireSchema(proof.SchemaVersion, CustomLoopDefinitionLineageProof.CurrentSchemaVersion, "definition-lineage proof");
        RequireIdentifier(proof.LoopId, nameof(proof.LoopId));
        RequireIdentifier(proof.RoleId, nameof(proof.RoleId));
        RequireIdentifier(proof.LastMutationOperationId, nameof(proof.LastMutationOperationId));
        RequireHash(proof.LastDefinitionHash, nameof(proof.LastDefinitionHash));
        if (proof.LastDefinitionVersion < 1)
        {
            throw new ArgumentException("Definition lineage must retain a positive last definition version.", nameof(proof));
        }

        if (proof.IsDeleted != proof.DeletedAtUtc.HasValue)
        {
            throw new ArgumentException("Deleted definition lineage must retain a deletion timestamp, and live lineage must not.", nameof(proof));
        }

        if (proof.DeletedAtUtc is { } deletedAtUtc)
        {
            RequireUtc(deletedAtUtc, nameof(proof.DeletedAtUtc));
        }
    }

    /// <summary>
    /// Validates class-specific compact-proof count and canonical byte accounting.
    /// </summary>
    /// <param name="artifactClass">The artifact class that owns the compact proof.</param>
    /// <param name="proofCount">The compact proof entry count.</param>
    /// <param name="proofUtf8Bytes">The canonical compact proof bytes, including entry separators.</param>
    public static void ValidateProofAccounting(CustomLoopReceiptArtifactClass artifactClass, int proofCount, long proofUtf8Bytes)
    {
        var budget = CustomLoopReceiptRetentionPolicy.GetBudget(artifactClass);
        if (proofCount < 0 || proofUtf8Bytes < 0 || !budget.CanAccountProof(0, 0, proofCount, proofUtf8Bytes))
        {
            throw new ArgumentException("Compact proof exceeds its class-specific count or UTF-8 byte ceiling.", nameof(proofCount));
        }
    }

    /// <summary>
    /// Validates a complete canonical compact proof ledger.
    /// </summary>
    /// <param name="ledger">The proof ledger.</param>
    public static void ValidateProofLedger(CustomLoopReceiptProofLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        RequireSchema(ledger.SchemaVersion, CustomLoopReceiptProofLedger.CurrentSchemaVersion, "proof ledger");
        RequireUtc(ledger.CreatedAtUtc, nameof(ledger.CreatedAtUtc));
        if (ledger.Generation < 1 || ledger.DefinitionLineage.IsDefault || ledger.ExpiredOperations.IsDefault)
        {
            throw new ArgumentException("Proof ledger generation and collections must be canonical.", nameof(ledger));
        }

        if (ledger.PreviousLedgerHash is not null)
        {
            RequireHash(ledger.PreviousLedgerHash, nameof(ledger.PreviousLedgerHash));
        }

        if (ledger.Generation == 1 && ledger.PreviousLedgerHash is not null
            || ledger.Generation > 1 && ledger.PreviousLedgerHash is null)
        {
            throw new ArgumentException("Only the first proof-ledger generation omits a previous-ledger hash.", nameof(ledger));
        }

        foreach (var proof in ledger.DefinitionLineage)
        {
            ValidateDefinitionLineageProof(proof);
            if (proof.DeletedAtUtc > ledger.CreatedAtUtc)
            {
                throw new ArgumentException("Definition lineage cannot postdate the proof ledger.", nameof(ledger));
            }
        }

        foreach (var proof in ledger.ExpiredOperations)
        {
            ValidateExpiredOperationProof(proof);
            if (proof.ExpiredAtUtc > ledger.CreatedAtUtc)
            {
                throw new ArgumentException("Expired-operation proof cannot enter a ledger before exact replay expires.", nameof(ledger));
            }
        }

        if (ledger.DefinitionLineage.Select(item => item.LoopId).Distinct(StringComparer.Ordinal).Count() != ledger.DefinitionLineage.Length
            || ledger.ExpiredOperations.Select(item => (item.ArtifactClass, item.OperationId)).Distinct().Count() != ledger.ExpiredOperations.Length)
        {
            throw new ArgumentException("Proof ledger contains duplicate lineage or operation identities.", nameof(ledger));
        }

        var expiredMutationIds = ledger.ExpiredOperations
            .Where(item => item.ArtifactClass == CustomLoopReceiptArtifactClass.DefinitionMutationReceipt)
            .Select(item => item.OperationId)
            .ToHashSet(StringComparer.Ordinal);
        if (ledger.DefinitionLineage.Any(item => item.IsDeleted && !expiredMutationIds.Contains(item.LastMutationOperationId)))
        {
            throw new ArgumentException("Deleted definition lineage must bind to retained expired mutation proof.", nameof(ledger));
        }

        var lineageUsage = MeasureProofUsage(ledger.DefinitionLineage, CustomLoopReceiptRetentionContractCodec.MeasureDefinitionLineageProofUtf8BytesUnchecked);
        var mutationUsage = MeasureProofUsage(ledger.ExpiredOperations.Where(item => item.ArtifactClass == CustomLoopReceiptArtifactClass.DefinitionMutationReceipt), CustomLoopReceiptRetentionContractCodec.MeasureExpiredOperationProofUtf8BytesUnchecked);
        var lifecycleUsage = MeasureProofUsage(ledger.ExpiredOperations.Where(item => item.ArtifactClass == CustomLoopReceiptArtifactClass.LifecycleControlReceipt), CustomLoopReceiptRetentionContractCodec.MeasureExpiredOperationProofUtf8BytesUnchecked);
        ValidateProofAccounting(CustomLoopReceiptArtifactClass.DefinitionTombstone, lineageUsage.Count, lineageUsage.Utf8Bytes);
        ValidateProofAccounting(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, mutationUsage.Count, mutationUsage.Utf8Bytes);
        ValidateProofAccounting(CustomLoopReceiptArtifactClass.LifecycleControlReceipt, lifecycleUsage.Count, lifecycleUsage.Utf8Bytes);

        var mutationBudget = CustomLoopReceiptRetentionPolicy.GetBudget(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt);
        var tombstoneBudget = CustomLoopReceiptRetentionPolicy.GetBudget(CustomLoopReceiptArtifactClass.DefinitionTombstone);
        var controlBudget = CustomLoopReceiptRetentionPolicy.GetBudget(CustomLoopReceiptArtifactClass.LifecycleControlReceipt);
        if (ledger.DefinitionLineage.Length > tombstoneBudget.MaximumProofCount
            || ledger.ExpiredOperations.Count(item => item.ArtifactClass == CustomLoopReceiptArtifactClass.DefinitionMutationReceipt) > mutationBudget.MaximumProofCount
            || ledger.ExpiredOperations.Count(item => item.ArtifactClass == CustomLoopReceiptArtifactClass.LifecycleControlReceipt) > controlBudget.MaximumProofCount)
        {
            throw new ArgumentException("Proof ledger exceeds a class proof-entry ceiling.", nameof(ledger));
        }
    }

    /// <summary>
    /// Validates the exact, expired, or unknown operation lookup contract.
    /// </summary>
    /// <param name="result">The lookup result.</param>
    public static void ValidateLookupResult(CustomLoopReceiptOperationLookupResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _ = CustomLoopReceiptRetentionPolicy.GetBudget(result.ArtifactClass);
        RequireIdentifier(result.OperationId, nameof(result.OperationId));
        RequireBoundedText(result.Detail, CustomLoopLimits.MaxRunDetailCharacters, nameof(result.Detail));
        if (result.Status is CustomLoopReceiptOperationLookupStatus.UnknownStatus
            || result.Status == CustomLoopReceiptOperationLookupStatus.Expired != (result.ExpiredProof is not null))
        {
            throw new ArgumentException("Expired lookup requires compact proof; exact and unknown lookups must not attach it.", nameof(result));
        }

        if (result.ExpiredProof is not null)
        {
            ValidateExpiredOperationProof(result.ExpiredProof);
            var compatibleClass = result.ArtifactClass == result.ExpiredProof.ArtifactClass
                || result.ArtifactClass == CustomLoopReceiptArtifactClass.DefinitionTombstone && result.ExpiredProof.ArtifactClass == CustomLoopReceiptArtifactClass.DefinitionMutationReceipt;
            if (!compatibleClass || !string.Equals(result.OperationId, result.ExpiredProof.OperationId, StringComparison.Ordinal))
            {
                throw new ArgumentException("Expired lookup proof does not match the requested class and operation identity.", nameof(result));
            }
        }
    }

    /// <summary>
    /// Validates one complete class posture projection.
    /// </summary>
    /// <param name="posture">The class posture.</param>
    public static void ValidateClassPosture(CustomLoopReceiptClassPosture posture)
    {
        ArgumentNullException.ThrowIfNull(posture);
        ValidateBudget(posture.Budget);
        if (posture.ArtifactClass != posture.Budget.ArtifactClass || posture.Categories.IsDefault || posture.Categories.Any(item => item is null))
        {
            throw new ArgumentException("Class posture and budget must identify the same artifact class.", nameof(posture));
        }

        var requiredCategories = Enum.GetValues<CustomLoopReceiptArtifactCategory>().Where(item => item != CustomLoopReceiptArtifactCategory.Unknown).ToArray();
        if (posture.Categories.Length != requiredCategories.Length
            || posture.Categories.Select(item => item.Category).Distinct().Count() != requiredCategories.Length
            || requiredCategories.Except(posture.Categories.Select(item => item.Category)).Any()
            || posture.Categories.Any(item => item.ArtifactCount < 0 || item.Utf8Bytes < 0 || item.ArtifactCount == 0 != (item.Utf8Bytes == 0)))
        {
            throw new ArgumentException("Class posture must contain one nonnegative usage entry for every required category.", nameof(posture));
        }

        var retainedLineage = posture.Categories.Single(item => item.Category == CustomLoopReceiptArtifactCategory.RetainedLineage);
        var expiredIdempotency = posture.Categories.Single(item => item.Category == CustomLoopReceiptArtifactCategory.ExpiredIdempotency);
        var live = posture.Categories.Single(item => item.Category == CustomLoopReceiptArtifactCategory.Live);
        var hasIncompatibleProof = posture.ArtifactClass == CustomLoopReceiptArtifactClass.DefinitionTombstone
            ? expiredIdempotency.ArtifactCount > 0
            : retainedLineage.ArtifactCount > 0;
        if (hasIncompatibleProof)
        {
            throw new ArgumentException("Class posture assigns compact proof to an artifact class that does not own that proof kind.", nameof(posture));
        }

        if (posture.OldestExactReplayExpiresAtUtc.HasValue != posture.NewestExactReplayExpiresAtUtc.HasValue)
        {
            throw new ArgumentException("Exact replay horizon endpoints must both be present or absent.", nameof(posture));
        }

        if (live.ArtifactCount > 0 != posture.OldestExactReplayExpiresAtUtc.HasValue)
        {
            throw new ArgumentException("Exact replay horizon endpoints must be present exactly when live receipts are retained.", nameof(posture));
        }

        if (posture.OldestExactReplayExpiresAtUtc is { } oldest && posture.NewestExactReplayExpiresAtUtc is { } newest)
        {
            RequireUtc(oldest, nameof(posture.OldestExactReplayExpiresAtUtc));
            RequireUtc(newest, nameof(posture.NewestExactReplayExpiresAtUtc));
            if (oldest > newest)
            {
                throw new ArgumentException("Exact replay horizon endpoints are inverted.", nameof(posture));
            }
        }

        RequireBoundedText(posture.Detail, CustomLoopLimits.MaxRunDetailCharacters, nameof(posture.Detail));
        if (!Enum.IsDefined(posture.ExhaustionReason) || !Enum.IsDefined(posture.CleanupBlockReason))
        {
            throw new ArgumentException("Class posture contains an unsupported exhaustion or cleanup block reason.", nameof(posture));
        }

        if (posture.ExhaustionReason == CustomLoopReceiptQuotaExhaustionReason.None
            && (posture.ArtifactCount > posture.Budget.MaximumArtifactCount
                || posture.ArtifactUtf8Bytes > posture.Budget.MaximumArtifactUtf8Bytes
                || posture.ProofCount > posture.Budget.MaximumProofCount
                || posture.ProofUtf8Bytes > posture.Budget.MaximumProofUtf8Bytes))
        {
            throw new ArgumentException("Over-limit class posture must expose an actionable exhaustion reason.", nameof(posture));
        }
    }

    /// <summary>
    /// Validates workspace-wide receipt-retention posture.
    /// </summary>
    /// <param name="posture">The workspace posture.</param>
    public static void ValidateWorkspacePosture(CustomLoopReceiptRetentionPosture posture)
    {
        ArgumentNullException.ThrowIfNull(posture);
        RequireUtc(posture.GeneratedAtUtc, nameof(posture.GeneratedAtUtc));
        if (posture.Classes.IsDefault
            || posture.Classes.Any(item => item is null)
            || posture.Classes.Length != 3
            || posture.Classes.Select(item => item.ArtifactClass).Distinct().Count() != 3
            || Enum.GetValues<CustomLoopReceiptArtifactClass>().Where(item => item != CustomLoopReceiptArtifactClass.Unknown).Except(posture.Classes.Select(item => item.ArtifactClass)).Any())
        {
            throw new ArgumentException("Workspace posture must contain exactly one posture for each receipt artifact class.", nameof(posture));
        }

        foreach (var classPosture in posture.Classes)
        {
            ValidateClassPosture(classPosture);
        }

        if (posture.ActiveCleanupJournalUtf8Bytes < 0 || posture.ActiveCleanupJournalUtf8Bytes > 3 * CustomLoopReceiptRetentionPolicy.MaxCleanupJournalUtf8Bytes)
        {
            throw new ArgumentException("Active cleanup journal accounting exceeds the bounded workspace journal allowance.", nameof(posture));
        }

        RequireBoundedText(posture.Detail, CustomLoopLimits.MaxRunDetailCharacters, nameof(posture.Detail));
        if (!Enum.IsDefined(posture.ExhaustionReason) || !Enum.IsDefined(posture.CleanupBlockReason))
        {
            throw new ArgumentException("Workspace posture contains an unsupported exhaustion or cleanup block reason.", nameof(posture));
        }

        if (posture.ExhaustionReason == CustomLoopReceiptQuotaExhaustionReason.None && posture.AccountedWorkspaceUtf8Bytes > posture.MaximumWorkspaceUtf8Bytes)
        {
            throw new ArgumentException("Over-limit workspace posture must expose an actionable exhaustion reason.", nameof(posture));
        }
    }

    /// <summary>
    /// Validates a durable cleanup journal and its immutable selected batch.
    /// </summary>
    /// <param name="journal">The cleanup journal.</param>
    public static void ValidateCleanupJournal(CustomLoopReceiptCleanupJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        RequireSchema(journal.SchemaVersion, CustomLoopReceiptCleanupJournal.CurrentSchemaVersion, "cleanup journal");
        ValidateCleanupRequest(journal.Request);
        RequireHash(journal.RequestHash, nameof(journal.RequestHash));
        if (!string.Equals(journal.RequestHash, CustomLoopReceiptRetentionContractCodec.ComputeCleanupRequestHash(journal.Request), StringComparison.Ordinal))
        {
            throw new ArgumentException("Cleanup journal request hash does not match its canonical request.", nameof(journal));
        }

        RequireIdentifier(journal.OwnerGenerationId, nameof(journal.OwnerGenerationId));
        RequireUtc(journal.OwnershipAcquiredAtUtc, nameof(journal.OwnershipAcquiredAtUtc));
        RequireUtc(journal.UpdatedAtUtc, nameof(journal.UpdatedAtUtc));
        RequireBoundedText(journal.Detail, CustomLoopLimits.MaxRunDetailCharacters, nameof(journal.Detail));
        if (journal.OwnerProcessId <= 0
            || journal.UpdatedAtUtc < journal.OwnershipAcquiredAtUtc
            || journal.UpdatedAtUtc - journal.OwnershipAcquiredAtUtc > CustomLoopReceiptRetentionPolicy.CleanupOwnershipWindow
            || journal.Candidates.IsDefault
            || journal.Candidates.Any(item => item is null)
            || journal.Candidates.Length > journal.Request.MaximumArtifactCount
            || journal.Candidates.Select(item => item.ArtifactId).Distinct(StringComparer.Ordinal).Count() != journal.Candidates.Length)
        {
            throw new ArgumentException("Cleanup journal ownership, chronology, or immutable candidate set is invalid.", nameof(journal));
        }

        long selectedBytes = 0;
        foreach (var candidate in journal.Candidates)
        {
            ValidateCandidate(journal.Request, candidate);
            selectedBytes = checked(selectedBytes + candidate.ArtifactUtf8Bytes);
        }

        if (selectedBytes > journal.Request.MaximumArtifactUtf8Bytes)
        {
            throw new ArgumentException("Cleanup candidate bytes exceed the request boundary.", nameof(journal));
        }

        if (journal.ProofLedgerHash is not null)
        {
            RequireHash(journal.ProofLedgerHash, nameof(journal.ProofLedgerHash));
        }

        ValidateJournalState(journal);
    }

    private static void ValidateCandidate(CustomLoopReceiptCleanupRequest request, CustomLoopReceiptCleanupCandidate candidate)
    {
        if (candidate is null)
        {
            throw new ArgumentException("Cleanup candidate cannot be null.", nameof(candidate));
        }

        RequireIdentifier(candidate.ArtifactId, nameof(candidate.ArtifactId));
        RequireHash(candidate.ArtifactHash, nameof(candidate.ArtifactHash));
        if (candidate.ArtifactUtf8Bytes < 1
            || !CustomLoopReceiptRetentionPolicy.IsSafelyPrunable(candidate.Category)
            || !candidate.OutcomeAuditRecorded
            || !candidate.OwnershipResolved
            || candidate.ExpiredOperationProof is null)
        {
            throw new ArgumentException("Cleanup candidates must be positive-size, audited, ownership-resolved, compactable evidence with expiry proof.", nameof(candidate));
        }

        ValidateExpiredOperationProof(candidate.ExpiredOperationProof);
        if (candidate.ExpiredOperationProof.CompletedAtUtc > request.ReplayCutoffUtc)
        {
            throw new ArgumentException("Cleanup candidate remains inside the exact replay horizon.", nameof(candidate));
        }

        if (candidate.DefinitionLineageProof is not null)
        {
            ValidateDefinitionLineageProof(candidate.DefinitionLineageProof);
            if (candidate.DefinitionLineageProof.DeletedAtUtc > candidate.ExpiredOperationProof.CompletedAtUtc)
            {
                throw new ArgumentException("Cleanup lineage cannot postdate the terminal mutation it proves.", nameof(candidate));
            }
        }

        var validClassProof = request.ArtifactClass switch
        {
            CustomLoopReceiptArtifactClass.DefinitionMutationReceipt => candidate.ExpiredOperationProof.ArtifactClass == CustomLoopReceiptArtifactClass.DefinitionMutationReceipt
                && string.Equals(candidate.ArtifactId, candidate.ExpiredOperationProof.OperationId, StringComparison.Ordinal)
                && (candidate.DefinitionLineageProof is null
                    || candidate.DefinitionLineageProof is { IsDeleted: true } mutationLineage
                    && string.Equals(mutationLineage.LastMutationOperationId, candidate.ExpiredOperationProof.OperationId, StringComparison.Ordinal)),
            CustomLoopReceiptArtifactClass.DefinitionTombstone => candidate.ExpiredOperationProof.ArtifactClass == CustomLoopReceiptArtifactClass.DefinitionMutationReceipt
                && candidate.DefinitionLineageProof is { IsDeleted: true } lineage
                && string.Equals(candidate.ArtifactId, lineage.LoopId, StringComparison.Ordinal)
                && string.Equals(candidate.ExpiredOperationProof.OperationId, lineage.LastMutationOperationId, StringComparison.Ordinal),
            CustomLoopReceiptArtifactClass.LifecycleControlReceipt => candidate.ExpiredOperationProof.ArtifactClass == CustomLoopReceiptArtifactClass.LifecycleControlReceipt
                && candidate.DefinitionLineageProof is null
                && string.Equals(candidate.ArtifactId, candidate.ExpiredOperationProof.OperationId, StringComparison.Ordinal),
            _ => false
        };
        if (!validClassProof)
        {
            throw new ArgumentException("Cleanup candidate does not retain the proof required by its target artifact class.", nameof(candidate));
        }
    }

    private static void ValidateJournalState(CustomLoopReceiptCleanupJournal journal)
    {
        if (!Enum.IsDefined(journal.Stage) || journal.Stage == CustomLoopReceiptCleanupStage.Unknown || !Enum.IsDefined(journal.Outcome))
        {
            throw new ArgumentException("Cleanup journal stage or outcome is unsupported.", nameof(journal));
        }

        var proofRequired = journal.Stage is CustomLoopReceiptCleanupStage.ProofLedgerWritten
            or CustomLoopReceiptCleanupStage.ArtifactsRemoved
            or CustomLoopReceiptCleanupStage.OutcomeAuditStarted
            or CustomLoopReceiptCleanupStage.CommittedWithAuditWarning
            || journal.Stage == CustomLoopReceiptCleanupStage.Completed && journal.Candidates.Length > 0
            || journal.Stage == CustomLoopReceiptCleanupStage.Degraded && journal.RemovedArtifactCount > 0;
        var proofOptional = journal.Stage is CustomLoopReceiptCleanupStage.AbandonedConflict or CustomLoopReceiptCleanupStage.Degraded;
        if ((proofRequired && journal.ProofLedgerHash is null)
            || (!proofRequired && !proofOptional && journal.ProofLedgerHash is not null))
        {
            throw new ArgumentException("Cleanup journal proof-ledger hash does not match its durable stage.", nameof(journal));
        }

        var removalCommitted = journal.Stage is CustomLoopReceiptCleanupStage.ArtifactsRemoved
            or CustomLoopReceiptCleanupStage.OutcomeAuditStarted
            or CustomLoopReceiptCleanupStage.Completed
            or CustomLoopReceiptCleanupStage.CommittedWithAuditWarning;
        var expectedRemovedCount = removalCommitted ? journal.Candidates.Length : 0;
        var expectedRemovedBytes = removalCommitted ? journal.Candidates.Sum(item => item.ArtifactUtf8Bytes) : 0;
        var degradedRemovalIsCanonical = journal.Stage == CustomLoopReceiptCleanupStage.Degraded
            && (journal.RemovedArtifactCount == 0 && journal.RemovedArtifactUtf8Bytes == 0
                || journal.RemovedArtifactCount == journal.Candidates.Length && journal.RemovedArtifactUtf8Bytes == journal.Candidates.Sum(item => item.ArtifactUtf8Bytes));
        if (!degradedRemovalIsCanonical && (journal.RemovedArtifactCount != expectedRemovedCount || journal.RemovedArtifactUtf8Bytes != expectedRemovedBytes))
        {
            throw new ArgumentException("Cleanup journal removal accounting does not match its durable stage and immutable candidates.", nameof(journal));
        }

        var validOutcome = journal.Stage switch
        {
            CustomLoopReceiptCleanupStage.IntentPersisted or CustomLoopReceiptCleanupStage.IntentAuditRecorded or CustomLoopReceiptCleanupStage.ProofLedgerWritten or CustomLoopReceiptCleanupStage.ArtifactsRemoved or CustomLoopReceiptCleanupStage.OutcomeAuditStarted => journal.Outcome == CustomLoopReceiptCleanupOutcome.Unknown,
            CustomLoopReceiptCleanupStage.Completed => journal.Outcome == (journal.Candidates.Length == 0 ? CustomLoopReceiptCleanupOutcome.NothingEligible : CustomLoopReceiptCleanupOutcome.Succeeded),
            CustomLoopReceiptCleanupStage.CommittedWithAuditWarning => journal.Outcome == CustomLoopReceiptCleanupOutcome.AuditUnavailable && journal.Candidates.Length > 0,
            CustomLoopReceiptCleanupStage.AbandonedConflict => journal.Outcome == CustomLoopReceiptCleanupOutcome.Conflict,
            CustomLoopReceiptCleanupStage.Degraded => journal.Outcome is CustomLoopReceiptCleanupOutcome.AuditUnavailable or CustomLoopReceiptCleanupOutcome.Corrupt or CustomLoopReceiptCleanupOutcome.Degraded,
            _ => false
        };
        if (!validOutcome)
        {
            throw new ArgumentException("Cleanup journal outcome does not match its durable stage.", nameof(journal));
        }
    }

    private static void RequireSchema(int actual, int expected, string artifact)
    {
        if (actual != expected)
        {
            throw new ArgumentException($"Unsupported {artifact} schema version `{actual}`; explicit reinitialization is required.");
        }
    }

    private static (int Count, long Utf8Bytes) MeasureProofUsage<T>(IEnumerable<T> proofs, Func<T, int> measure)
    {
        var count = 0;
        long utf8Bytes = 0;
        foreach (var proof in proofs)
        {
            utf8Bytes = checked(utf8Bytes + measure(proof) + (count == 0 ? 0 : 1));
            count++;
        }

        return (count, utf8Bytes);
    }

    private static void RequireIdentifier(string value, string parameterName)
    {
        if (!CustomLoopArtifactIdentifier.IsValid(value, CustomLoopLimits.MaxMutationOperationIdCharacters))
        {
            throw new ArgumentException("Receipt retention identity is not a canonical artifact identifier.", parameterName);
        }
    }

    private static void RequireHash(string value, string parameterName)
    {
        if (value is not { Length: CustomLoopLimits.Sha256HexCharacters } || !value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw new ArgumentException("Receipt retention hash must be lowercase hexadecimal SHA-256.", parameterName);
        }
    }

    private static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Receipt retention timestamp must be a non-default UTC value.", parameterName);
        }
    }

    private static void RequireBoundedText(string value, int maximumCharacters, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumCharacters
            || !value.IsNormalized(NormalizationForm.FormC)
            || value.Any(character => char.IsControl(character) || char.IsSurrogate(character)))
        {
            throw new ArgumentException("Receipt retention text is empty, oversized, noncanonical, or unsafe.", parameterName);
        }
    }
}
