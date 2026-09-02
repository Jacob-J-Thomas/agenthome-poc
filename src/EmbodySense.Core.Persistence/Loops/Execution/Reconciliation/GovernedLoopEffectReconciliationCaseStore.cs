using System.Globalization;
using System.Text;
using EmbodySense.Core.Application.Loops.EffectAttempts.Models;
using EmbodySense.Core.Application.Loops.Execution.Reconciliation;
using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops.EffectAttempts;
using EmbodySense.Core.Persistence.Loops.EffectAttempts.Models;
using EmbodySense.Core.Persistence.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Persistence.Loops.Execution.Reconciliation;

/// <summary>Persists canonical reconciliation cases in the existing effect-attempt artifact envelope.</summary>
/// <remarks>
/// Case versions, operation receipts, and the small recovery journal live beside effect-attempt versions under the
/// existing effect-attempt root. All mutations run through the supplied effect-attempt store so the same cross-process
/// mutation lease protects both the case and any proof-backed reconciled effect successor. No separate reconciliation
/// directory, lock, or authority ledger is created.
/// </remarks>
public sealed class GovernedLoopEffectReconciliationCaseStore : IGovernedLoopEffectReconciliationCaseStore, IGovernedLoopEffectReconciliationResolutionReader
{
    private readonly GovernedLoopEffectAttemptStore _effectAttempts;
    private readonly CustomLoopArtifactPathGuard _guard;
    private readonly string _root;
    private readonly string _workspaceId;
    private readonly TimeProvider _timeProvider;
    private readonly GovernedLoopEffectReconciliationCaseStoreOptions _options;

    /// <summary>Creates a reconciliation store over one already-composed canonical effect-attempt store.</summary>
    /// <param name="effectAttempts">The single server-owned effect-attempt store whose root and mutation lease are reused.</param>
    /// <param name="timeProvider">The trusted clock used only for internal recovery receipts and journals.</param>
    /// <param name="options">Optional durable-boundary observation used by controlled process-loss fixtures.</param>
    public GovernedLoopEffectReconciliationCaseStore(
        GovernedLoopEffectAttemptStore effectAttempts,
        TimeProvider? timeProvider = null,
        GovernedLoopEffectReconciliationCaseStoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(effectAttempts);
        _effectAttempts = effectAttempts;
        _guard = effectAttempts.ArtifactPathGuard;
        _root = effectAttempts.ArtifactRoot;
        _workspaceId = effectAttempts.WorkspaceId;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options ?? new GovernedLoopEffectReconciliationCaseStoreOptions();
    }

    /// <summary>Creates a canonical effect-attempt store and exposes reconciliation over that same artifact envelope.</summary>
    /// <param name="paths">The server-owned workspace paths.</param>
    /// <param name="options">Optional finite effect-attempt store limits.</param>
    /// <param name="timeProvider">The trusted clock used only for internal recovery receipts and journals.</param>
    /// <param name="reconciliationOptions">Optional durable-boundary observation used by controlled process-loss fixtures.</param>
    public GovernedLoopEffectReconciliationCaseStore(
        WorkspacePaths paths,
        GovernedLoopEffectAttemptStoreOptions? options = null,
        TimeProvider? timeProvider = null,
        GovernedLoopEffectReconciliationCaseStoreOptions? reconciliationOptions = null)
        : this(new GovernedLoopEffectAttemptStore(paths, options), timeProvider, reconciliationOptions)
    {
    }

    /// <summary>Probes the complete shared artifact envelope and every reconciliation case chain without changing evidence.</summary>
    /// <param name="cancellationToken">Cancels bounded lock acquisition and validation.</param>
    /// <returns><see langword="true"/> when the empty or populated canonical envelope is safely readable.</returns>
    public async Task<bool> ProbeStorageAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_guard.DirectoryExists(_root))
        {
            return true;
        }

        try
        {
            using var readLock = await AcquireExistingReadLockAsync(cancellationToken).ConfigureAwait(false);
            await _effectAttempts.ValidateCurrentEffectChainsForReconciliationAsync(cancellationToken).ConfigureAwait(false);
            if (HasPendingJournal())
            {
                await ValidatePendingJournalsAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            var cases = await ReadAllCurrentCasesAsync(cancellationToken).ConfigureAwait(false);
            await ValidateReceiptInventoryAsync(cases, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsCorrupt(exception) || IsUnavailable(exception))
        {
            return false;
        }
    }

    /// <summary>Completes any bounded interrupted case/effect publication under the canonical mutation lease.</summary>
    /// <param name="cancellationToken">Cancels before a journal is resumed.</param>
    /// <returns><see langword="true"/> when no pending journal remains.</returns>
    public async Task<bool> RecoverAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _effectAttempts.ExecuteReconciliationMutationAsync(
                async (store, token) =>
                {
                    await RecoverPendingJournalsUnderLockAsync(token).ConfigureAwait(false);
                    var cases = await ReadAllCurrentCasesAsync(token).ConfigureAwait(false);
                    await ValidateReceiptInventoryAsync(cases, token).ConfigureAwait(false);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsCorrupt(exception) || IsUnavailable(exception))
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<GovernedLoopEffectReconciliationCaseListPage> ListAsync(
        GovernedLoopEffectReconciliationCaseListRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_guard.DirectoryExists(_root))
        {
            return ReadyPage([], null);
        }

        try
        {
            using var readLock = await AcquireExistingReadLockAsync(cancellationToken).ConfigureAwait(false);
            _effectAttempts.ValidateArtifactInventoryForReconciliation(cancellationToken);
            if (HasPendingJournal())
            {
                await ValidatePendingJournalsAsync(cancellationToken).ConfigureAwait(false);
                return new GovernedLoopEffectReconciliationCaseListPage(GovernedLoopEffectReconciliationCaseListStatus.Unavailable, [], null);
            }

            var persistedCases = await ReadAllCurrentCasesAsync(cancellationToken).ConfigureAwait(false);
            await ValidateReceiptInventoryAsync(persistedCases, cancellationToken).ConfigureAwait(false);
            var cases = persistedCases
                .OrderBy(value => value.CaseId, StringComparer.Ordinal)
                .Select(ToSummary)
                .ToArray();
            if (!TryReadCursor(request.Cursor, out var cursorCaseId))
            {
                return new GovernedLoopEffectReconciliationCaseListPage(GovernedLoopEffectReconciliationCaseListStatus.Invalid, [], null);
            }

            var start = cursorCaseId is null
                ? 0
                : Array.FindIndex(cases, value => string.Equals(value.CaseId, cursorCaseId, StringComparison.Ordinal)) + 1;
            if (cursorCaseId is not null && start == 0)
            {
                return new GovernedLoopEffectReconciliationCaseListPage(GovernedLoopEffectReconciliationCaseListStatus.Invalid, [], null);
            }

            var page = cases.Skip(start).Take(request.MaximumCount).ToArray();
            var hasMore = start + page.Length < cases.Length;
            var next = hasMore ? CreateCursor(page[^1].CaseId) : null;
            return ReadyPage(page, next);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsCorrupt(exception))
        {
            return new GovernedLoopEffectReconciliationCaseListPage(GovernedLoopEffectReconciliationCaseListStatus.Corrupt, [], null);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return new GovernedLoopEffectReconciliationCaseListPage(GovernedLoopEffectReconciliationCaseListStatus.Unavailable, [], null);
        }
    }

    /// <inheritdoc />
    public async Task<GovernedLoopEffectReconciliationCaseReadResult> ReadAsync(
        GovernedLoopEffectReconciliationCaseReadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_guard.DirectoryExists(_root))
        {
            return new GovernedLoopEffectReconciliationCaseReadResult(GovernedLoopEffectReconciliationCaseReadStatus.NotFound, null);
        }

        try
        {
            using var readLock = await AcquireExistingReadLockAsync(cancellationToken).ConfigureAwait(false);
            _effectAttempts.ValidateArtifactInventoryForReconciliation(cancellationToken);
            if (HasPendingJournal())
            {
                await ValidatePendingJournalsAsync(cancellationToken).ConfigureAwait(false);
                return new GovernedLoopEffectReconciliationCaseReadResult(GovernedLoopEffectReconciliationCaseReadStatus.Unavailable, null);
            }

            var allCases = await ReadAllCurrentCasesAsync(cancellationToken).ConfigureAwait(false);
            await ValidateReceiptInventoryAsync(allCases, cancellationToken).ConfigureAwait(false);
            var key = GovernedLoopEffectReconciliationArtifactNames.StorageKey(request.Reference.CaseId);
            var chain = await ReadCaseChainAsync(key, allowMissingHead: false, cancellationToken).ConfigureAwait(false);
            var exact = chain.FirstOrDefault(value => value.CaseVersion == request.Reference.CaseVersion
                && string.Equals(value.ContentHash, request.Reference.ContentHash, StringComparison.Ordinal));
            if (exact is null || !string.Equals(exact.Binding.ContentHash, request.Reference.BindingHash, StringComparison.Ordinal))
            {
                return new GovernedLoopEffectReconciliationCaseReadResult(GovernedLoopEffectReconciliationCaseReadStatus.NotFound, null);
            }
            _ = await ReadAndValidateExpectedEffectAsync(exact, cancellationToken).ConfigureAwait(false);

            return new GovernedLoopEffectReconciliationCaseReadResult(GovernedLoopEffectReconciliationCaseReadStatus.Found, exact);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsCorrupt(exception))
        {
            return new GovernedLoopEffectReconciliationCaseReadResult(GovernedLoopEffectReconciliationCaseReadStatus.Corrupt, null);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return new GovernedLoopEffectReconciliationCaseReadResult(GovernedLoopEffectReconciliationCaseReadStatus.Unavailable, null);
        }
    }

    /// <inheritdoc />
    public async Task<GovernedLoopEffectReconciliationCaseMutationResult> CompareExchangeAsync(
        GovernedLoopEffectReconciliationCaseMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await _effectAttempts.ExecuteReconciliationMutationAsync(
                (_, token) => MutateUnderLockAsync(request, token),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (GovernedLoopEffectReconciliationRepairRequiredException)
        {
            return MutationResult(GovernedLoopEffectReconciliationCaseMutationStatus.RepairRequired);
        }
        catch (Exception exception) when (IsCorrupt(exception))
        {
            return MutationResult(GovernedLoopEffectReconciliationCaseMutationStatus.Corrupt);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return MutationResult(GovernedLoopEffectReconciliationCaseMutationStatus.Unavailable);
        }
    }

    /// <inheritdoc />
    public async Task<GovernedLoopEffectReconciliationResolutionReadResult> ReadAsync(
        GovernedLoopEffectReconciliationResolutionReadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_guard.DirectoryExists(_root))
        {
            return new GovernedLoopEffectReconciliationResolutionReadResult(GovernedLoopEffectReconciliationResolutionReadStatus.NotFound, null);
        }

        try
        {
            using var readLock = await AcquireExistingReadLockAsync(cancellationToken).ConfigureAwait(false);
            _effectAttempts.ValidateArtifactInventoryForReconciliation(cancellationToken);
            if (HasPendingJournal())
            {
                await ValidatePendingJournalsAsync(cancellationToken).ConfigureAwait(false);
                return new GovernedLoopEffectReconciliationResolutionReadResult(GovernedLoopEffectReconciliationResolutionReadStatus.Unavailable, null);
            }

            var allCases = await ReadAllCurrentCasesAsync(cancellationToken).ConfigureAwait(false);
            await ValidateReceiptInventoryAsync(allCases, cancellationToken).ConfigureAwait(false);
            var key = GovernedLoopEffectReconciliationArtifactNames.StorageKey(request.Case.CaseId);
            var chain = await ReadCaseChainAsync(key, allowMissingHead: false, cancellationToken).ConfigureAwait(false);
            var exact = chain.FirstOrDefault(value => value.CaseVersion == request.Case.CaseVersion
                && string.Equals(value.ContentHash, request.Case.ContentHash, StringComparison.Ordinal));
            if (exact is null
                || !string.Equals(exact.Binding.ContentHash, request.Case.BindingHash, StringComparison.Ordinal)
                || !Equals(exact.Binding, request.Binding)
                || exact.Resolution is null)
            {
                return new GovernedLoopEffectReconciliationResolutionReadResult(GovernedLoopEffectReconciliationResolutionReadStatus.NotFound, null);
            }
            _ = await ReadAndValidateExpectedEffectAsync(exact, cancellationToken).ConfigureAwait(false);

            return new GovernedLoopEffectReconciliationResolutionReadResult(GovernedLoopEffectReconciliationResolutionReadStatus.Found, exact.Resolution);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsCorrupt(exception))
        {
            return new GovernedLoopEffectReconciliationResolutionReadResult(GovernedLoopEffectReconciliationResolutionReadStatus.Corrupt, null);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return new GovernedLoopEffectReconciliationResolutionReadResult(GovernedLoopEffectReconciliationResolutionReadStatus.Unavailable, null);
        }
    }

    private async Task<GovernedLoopEffectReconciliationCaseMutationResult> MutateUnderLockAsync(
        GovernedLoopEffectReconciliationCaseMutationRequest request,
        CancellationToken cancellationToken)
    {
        await RecoverPendingJournalsUnderLockAsync(cancellationToken).ConfigureAwait(false);
        var allCases = await ReadAllCurrentCasesAsync(cancellationToken).ConfigureAwait(false);
        await ValidateReceiptInventoryAsync(allCases, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(request.Binding.WorkspaceId, _workspaceId, StringComparison.Ordinal)
            || !string.Equals(request.Binding.ContentHash, request.Replacement.Binding.ContentHash, StringComparison.Ordinal))
        {
            return MutationResult(GovernedLoopEffectReconciliationCaseMutationStatus.Invalid);
        }

        var operationKey = GovernedLoopEffectReconciliationArtifactNames.OperationKey(request.OperationId);
        var receipt = await ReadReceiptAsync(operationKey, cancellationToken).ConfigureAwait(false);
        if (receipt is not null)
        {
            if (!SameReceiptRequest(receipt, request))
            {
                return await ConflictFromReceiptAsync(receipt, cancellationToken).ConfigureAwait(false);
            }

            return await ReplayReceiptAsync(receipt, cancellationToken).ConfigureAwait(false);
        }

        var caseKey = GovernedLoopEffectReconciliationArtifactNames.StorageKey(request.Replacement.CaseId);
        var chain = await ReadCaseChainAsync(caseKey, allowMissingHead: true, cancellationToken).ConfigureAwait(false);
        var currentCase = chain.LastOrDefault();
        var currentEffect = await _effectAttempts.ReadCurrentForReconciliationAsync(
            request.Binding.OperationId,
            request.Binding.EffectGeneration,
            cancellationToken).ConfigureAwait(false);
        if (currentEffect is null)
        {
            return MutationResult(GovernedLoopEffectReconciliationCaseMutationStatus.Unavailable);
        }
        if (currentCase is not null)
        {
            _ = await ReadAndValidateExpectedEffectAsync(currentCase, cancellationToken).ConfigureAwait(false);
        }

        if (currentCase is null)
        {
            if (request.ExpectedCaseVersion is not null || request.ExpectedCaseContentHash is not null || request.Replacement.CaseVersion != 1)
            {
                return MutationResult(GovernedLoopEffectReconciliationCaseMutationStatus.Invalid);
            }
        }
        else
        {
            if (request.ExpectedCaseVersion != currentCase.CaseVersion
                || !string.Equals(request.ExpectedCaseContentHash, currentCase.ContentHash, StringComparison.Ordinal))
            {
                return CurrentResult(GovernedLoopEffectReconciliationCaseMutationStatus.Conflict, currentCase, currentEffect);
            }

            if (!GovernedLoopEffectReconciliationContractValidator.ValidateTransition(currentCase, request.Replacement).IsValid)
            {
                return MutationResult(GovernedLoopEffectReconciliationCaseMutationStatus.Invalid);
            }
        }

        if (!GovernedLoopEffectReconciliationContractValidator.Validate(request.Replacement, currentEffect).IsValid)
        {
            return MutationResult(GovernedLoopEffectReconciliationCaseMutationStatus.Invalid);
        }

        if (request.ReconciledEffectSuccessor is not null
                && !GovernedLoopEffectReconciliationAttemptContract.IsDirectSuccessor(currentEffect, request.ReconciledEffectSuccessor, request.Replacement))
        {
            return MutationResult(GovernedLoopEffectReconciliationCaseMutationStatus.Invalid);
        }

        var receiptCount = CountReceiptFiles();
        if (receiptCount >= GovernedLoopEffectReconciliationPersistenceLimits.MaximumOperationReceipts)
        {
            return MutationResult(GovernedLoopEffectReconciliationCaseMutationStatus.CapacityExceeded);
        }
        if (currentCase is null && CountCaseHeads() >= GovernedLoopEffectReconciliationPersistenceLimits.MaximumCases
            || currentCase is not null && chain.Count >= GovernedLoopEffectReconciliationPersistenceLimits.MaximumCaseVersions)
        {
            return MutationResult(GovernedLoopEffectReconciliationCaseMutationStatus.CapacityExceeded);
        }

        var journal = CreateJournal(request, operationKey);
        var effectHeadHash = request.ReconciledEffectSuccessor?.ContentHash ?? currentEffect.ContentHash;
        var storedReceipt = CreateReceipt(request, effectHeadHash, journal.CreatedAtUtc);
        if (!HasRetainedCapacity(request, journal, storedReceipt))
        {
            return MutationResult(GovernedLoopEffectReconciliationCaseMutationStatus.CapacityExceeded);
        }

        await WriteJournalIfAbsentAsync(operationKey, journal, cancellationToken).ConfigureAwait(false);
        Observe(GovernedLoopEffectReconciliationPersistenceBoundary.JournalPublished);
        try
        {
            await PublishCaseAsync(request.Replacement, caseKey, cancellationToken).ConfigureAwait(false);
            Observe(GovernedLoopEffectReconciliationPersistenceBoundary.CasePublished);
            journal = journal with { Stage = GovernedLoopEffectReconciliationJournalStage.CasePublished, ContentHash = string.Empty };
            await WriteJournalAsync(operationKey, journal, cancellationToken).ConfigureAwait(false);

            var effectHead = currentEffect;
            if (request.ReconciledEffectSuccessor is not null)
            {
                var effectCommit = await _effectAttempts.CommitReconciliationSuccessorAsync(
                    currentEffect,
                    request.ReconciledEffectSuccessor,
                    request.Replacement,
                    cancellationToken,
                    () => Observe(GovernedLoopEffectReconciliationPersistenceBoundary.EffectVersionPublished)).ConfigureAwait(false);
                if (effectCommit.Status is not (GovernedLoopEffectAttemptStoreStatus.Created or GovernedLoopEffectAttemptStoreStatus.Replayed)
                    || effectCommit.Attempt is null)
                {
                    throw new GovernedLoopEffectReconciliationRepairRequiredException("The proof-backed effect successor could not be atomically established with the reconciliation case.");
                }

                effectHead = effectCommit.Attempt;
                Observe(GovernedLoopEffectReconciliationPersistenceBoundary.EffectPublished);
                journal = journal with { Stage = GovernedLoopEffectReconciliationJournalStage.EffectPublished, ContentHash = string.Empty };
                await WriteJournalAsync(operationKey, journal, cancellationToken).ConfigureAwait(false);
            }

            storedReceipt = CreateReceipt(request, effectHead.ContentHash, journal.CreatedAtUtc);
            await WriteReceiptIfAbsentAsync(operationKey, storedReceipt, cancellationToken).ConfigureAwait(false);
            Observe(GovernedLoopEffectReconciliationPersistenceBoundary.ReceiptPublished);
            journal = journal with { Stage = GovernedLoopEffectReconciliationJournalStage.ReceiptPublished, ContentHash = string.Empty };
            await WriteJournalAsync(operationKey, journal, cancellationToken).ConfigureAwait(false);
            DeleteJournal(operationKey);
            return CurrentResult(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, request.Replacement, effectHead);
        }
        catch
        {
            throw;
        }
    }

    private bool HasRetainedCapacity(
        GovernedLoopEffectReconciliationCaseMutationRequest request,
        GovernedLoopEffectReconciliationJournal journal,
        GovernedLoopEffectReconciliationOperationReceipt receipt)
    {
        try
        {
            var caseBytes = GovernedLoopEffectReconciliationRecordCodec.Encode(request.Replacement).Length;
            var receiptBytes = GovernedLoopEffectReconciliationReceiptCodec.Encode(receipt).Length;
            var successorBytes = request.ReconciledEffectSuccessor is null
                ? 0
                : EmbodySense.Core.Common.Loops.Execution.Effects.GovernedLoopEffectAttemptRecordCodec.Encode(request.ReconciledEffectSuccessor).Length;
            var journalBytes = Enum.GetValues<GovernedLoopEffectReconciliationJournalStage>()
                .Select(stage => GovernedLoopEffectReconciliationJournalCodec.Encode(journal with { Stage = stage, ContentHash = string.Empty }).Length)
                .Max();
            var headBytes = request.ExpectedCaseVersion is null
                ? GovernedLoopEffectReconciliationContractLimits.Sha256HexCharacters
                : 0;
            var additionalBytes = checked((long)caseBytes + receiptBytes + successorBytes + journalBytes + headBytes);
            if (additionalBytes > _effectAttempts.MaximumStoreBytes)
            {
                return false;
            }

            return _effectAttempts.GetRetainedBytesUnderMutationLock() <= _effectAttempts.MaximumStoreBytes - additionalBytes;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private async Task RecoverPendingJournalsUnderLockAsync(CancellationToken cancellationToken)
    {
        if (!_guard.DirectoryExists(_root))
        {
            return;
        }

        var journals = Directory.EnumerateFiles(_root, GovernedLoopEffectReconciliationPersistenceLimits.JournalFilePrefix + "*.json", SearchOption.TopDirectoryOnly)
            .Select(path => Path.GetFileName(path))
            .Where(name => name is not null && GovernedLoopEffectReconciliationArtifactNames.TryParseJournalFile(name, out _))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (journals.Length > GovernedLoopEffectReconciliationPersistenceLimits.MaximumJournals)
        {
            throw new GovernedLoopEffectReconciliationRepairRequiredException("Too many interrupted reconciliation publications require explicit repair.");
        }

        foreach (var journalFile in journals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operationKey = GovernedLoopEffectReconciliationArtifactNames.TryParseJournalFile(journalFile!, out var parsedKey)
                ? parsedKey
                : throw new FormatException("The reconciliation transaction journal name is malformed.");
            var journal = await ReadJournalAsync(operationKey, cancellationToken).ConfigureAwait(false)
                ?? throw new FormatException("The reconciliation transaction journal disappeared while under the mutation lease.");
            var replacement = DecodeCase(journal.ReplacementJson);
            var successor = journal.SuccessorJson is null ? null : DecodeAttempt(journal.SuccessorJson);
            if (!string.Equals(replacement.CaseId, journal.CaseId, StringComparison.Ordinal)
                || !string.Equals(GovernedLoopEffectReconciliationArtifactNames.StorageKey(replacement.CaseId), journal.StorageKey, StringComparison.Ordinal)
                || !string.Equals(replacement.ContentHash, journal.ReplacementHash, StringComparison.Ordinal)
                || replacement.CaseVersion != journal.ReplacementVersion)
            {
                throw new GovernedLoopEffectReconciliationRepairRequiredException("The reconciliation journal is not bound to its immutable case payload.");
            }

            var caseKey = journal.StorageKey;
            var chain = await ReadCaseChainAsync(caseKey, allowMissingHead: true, cancellationToken).ConfigureAwait(false);
            var currentCase = chain.LastOrDefault();
            var replacementIsCurrent = currentCase is not null
                && currentCase.CaseVersion == replacement.CaseVersion
                && string.Equals(currentCase.ContentHash, replacement.ContentHash, StringComparison.Ordinal);
            var expectedIsCurrent = journal.ExpectedCaseHash is null
                ? currentCase is null
                : currentCase is not null && string.Equals(currentCase.ContentHash, journal.ExpectedCaseHash, StringComparison.Ordinal);
            if (!replacementIsCurrent && !expectedIsCurrent)
            {
                throw new GovernedLoopEffectReconciliationRepairRequiredException("The interrupted reconciliation case has a conflicting immutable head.");
            }

            var expectedEffect = await _effectAttempts.ReadExactForReconciliationAsync(replacement.Binding.OperationId, replacement.Binding.EffectGeneration, journal.ExpectedEffectHash!, cancellationToken).ConfigureAwait(false)
                ?? throw new GovernedLoopEffectReconciliationRepairRequiredException("The interrupted reconciliation case lost its expected immutable effect-attempt evidence.");
            if (!GovernedLoopEffectReconciliationContractValidator.Validate(replacement, expectedEffect).IsValid)
            {
                throw new GovernedLoopEffectReconciliationRepairRequiredException("The interrupted reconciliation case is not bound to the canonical current effect-attempt head.");
            }
            if (currentCase is not null
                && !string.Equals(currentCase.ContentHash, replacement.ContentHash, StringComparison.Ordinal)
                && !GovernedLoopEffectReconciliationContractValidator.ValidateTransition(currentCase, replacement).IsValid)
            {
                throw new GovernedLoopEffectReconciliationRepairRequiredException("The interrupted reconciliation case is not an immutable successor of its expected case head.");
            }
            if (currentCase is null && (replacement.CaseVersion != 1 || replacement.PreviousContentHash is not null))
            {
                throw new GovernedLoopEffectReconciliationRepairRequiredException("The interrupted reconciliation case create is not a version-one root.");
            }
            await PublishCaseAsync(replacement, caseKey, cancellationToken).ConfigureAwait(false);
            if (successor is not null)
            {
                if (!GovernedLoopEffectReconciliationAttemptContract.IsDirectSuccessor(expectedEffect, successor, replacement))
                {
                    throw new GovernedLoopEffectReconciliationRepairRequiredException("The interrupted reconciliation successor is not an exact proof-backed effect transition.");
                }

                _ = await _effectAttempts.RepairOrphanedReconciliationSuccessorHeadAsync(expectedEffect, successor, replacement, cancellationToken).ConfigureAwait(false);
            }

            var effect = await _effectAttempts.ReadCurrentForReconciliationAsync(replacement.Binding.OperationId, replacement.Binding.EffectGeneration, cancellationToken).ConfigureAwait(false)
                ?? throw new GovernedLoopEffectReconciliationRepairRequiredException("The interrupted reconciliation case lost its canonical effect-attempt head.");
            if (successor is null)
            {
                if (!string.Equals(effect.ContentHash, expectedEffect.ContentHash, StringComparison.Ordinal))
                {
                    throw new GovernedLoopEffectReconciliationRepairRequiredException("The interrupted reconciliation case observed an unexpected current effect-attempt head.");
                }
            }
            else
            {
                if (!string.Equals(effect.ContentHash, successor.ContentHash, StringComparison.Ordinal))
                {
                    if (!string.Equals(effect.ContentHash, expectedEffect.ContentHash, StringComparison.Ordinal))
                    {
                        throw new GovernedLoopEffectReconciliationRepairRequiredException("The interrupted reconciliation effect transitioned to an unexpected immutable head.");
                    }

                    var commit = await _effectAttempts.CommitReconciliationSuccessorAsync(expectedEffect, successor, replacement, cancellationToken).ConfigureAwait(false);
                    if (commit.Status is not (GovernedLoopEffectAttemptStoreStatus.Created or GovernedLoopEffectAttemptStoreStatus.Replayed)
                        || commit.Attempt is null)
                    {
                        throw new GovernedLoopEffectReconciliationRepairRequiredException("The interrupted reconciliation effect successor could not be completed safely.");
                    }

                    effect = commit.Attempt;
                }
            }

            var receipt = CreateReceiptFromJournal(journal, effect.ContentHash);
            await WriteReceiptIfAbsentAsync(operationKey, receipt, cancellationToken).ConfigureAwait(false);
            DeleteJournal(operationKey);
        }
    }

    private async Task<GovernedLoopEffectReconciliationCaseMutationResult> ReplayReceiptAsync(
        GovernedLoopEffectReconciliationOperationReceipt receipt,
        CancellationToken cancellationToken)
    {
        var key = GovernedLoopEffectReconciliationArtifactNames.StorageKey(receipt.CaseId);
        var chain = await ReadCaseChainAsync(key, allowMissingHead: false, cancellationToken).ConfigureAwait(false);
        var exact = chain.FirstOrDefault(value => value.CaseVersion == receipt.CaseVersion
            && string.Equals(value.ContentHash, receipt.CaseContentHash, StringComparison.Ordinal));
        var effect = exact is null || receipt.EffectContentHash is null
            ? null
            : await _effectAttempts.ReadExactForReconciliationAsync(exact.Binding.OperationId, exact.Binding.EffectGeneration, receipt.EffectContentHash, cancellationToken).ConfigureAwait(false);
        if (exact is null || effect is null)
        {
            throw new GovernedLoopEffectReconciliationRepairRequiredException("The durable reconciliation receipt no longer points to a readable immutable result.");
        }

        return CurrentResult(GovernedLoopEffectReconciliationCaseMutationStatus.Replayed, exact, effect);
    }

    private async Task<GovernedLoopEffectReconciliationCaseMutationResult> ConflictFromReceiptAsync(
        GovernedLoopEffectReconciliationOperationReceipt receipt,
        CancellationToken cancellationToken)
    {
        var key = GovernedLoopEffectReconciliationArtifactNames.StorageKey(receipt.CaseId);
        var chain = await ReadCaseChainAsync(key, allowMissingHead: false, cancellationToken).ConfigureAwait(false);
        var current = chain.LastOrDefault();
        if (current is null)
        {
            return MutationResult(GovernedLoopEffectReconciliationCaseMutationStatus.Unavailable);
        }

        var effect = await _effectAttempts.ReadCurrentForReconciliationAsync(current.Binding.OperationId, current.Binding.EffectGeneration, cancellationToken).ConfigureAwait(false);
        return effect is null
            ? MutationResult(GovernedLoopEffectReconciliationCaseMutationStatus.Unavailable)
            : CurrentResult(GovernedLoopEffectReconciliationCaseMutationStatus.Conflict, current, effect);
    }

    private async Task<GovernedLoopEffectReconciliationCase[]> ReadAllCurrentCasesAsync(CancellationToken cancellationToken)
    {
        if (!_guard.DirectoryExists(_root))
        {
            return [];
        }

        var heads = Directory.EnumerateFiles(_root, GovernedLoopEffectReconciliationPersistenceLimits.CaseFilePrefix + "*.head", SearchOption.TopDirectoryOnly)
            .Select(path => Path.GetFileName(path))
            .Where(name => name is not null && GovernedLoopEffectReconciliationArtifactNames.TryParseCaseHeadFile(name, out _))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (heads.Length > GovernedLoopEffectReconciliationPersistenceLimits.MaximumCases)
        {
            throw new FormatException("The reconciliation case inventory exceeds its finite bound.");
        }

        var headKeys = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<GovernedLoopEffectReconciliationCase>(heads.Length);
        foreach (var head in heads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!GovernedLoopEffectReconciliationArtifactNames.TryParseCaseHeadFile(head!, out var key))
            {
                throw new FormatException("The reconciliation case head name is malformed.");
            }
            headKeys.Add(key);

            var chain = await ReadCaseChainAsync(key, allowMissingHead: false, cancellationToken).ConfigureAwait(false);
            var current = chain[^1];
            _ = await ReadAndValidateExpectedEffectAsync(current, cancellationToken).ConfigureAwait(false);
            result.Add(current);
        }

        foreach (var path in Directory.EnumerateFiles(_root, GovernedLoopEffectReconciliationPersistenceLimits.CaseFilePrefix + "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GovernedLoopEffectReconciliationArtifactNames.TryParseCaseVersionFile(Path.GetFileName(path), out var key, out _, out _)
                && !headKeys.Contains(key))
            {
                throw new FormatException("The reconciliation case inventory contains an immutable version without a durable head.");
            }
        }

        return result.ToArray();
    }

    private async Task ValidateReceiptInventoryAsync(
        IReadOnlyList<GovernedLoopEffectReconciliationCase> cases,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(_root, GovernedLoopEffectReconciliationPersistenceLimits.ReceiptFilePrefix + "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!GovernedLoopEffectReconciliationArtifactNames.TryParseReceiptFile(Path.GetFileName(path), out var operationKey))
            {
                continue;
            }

            var receipt = await ReadReceiptAsync(operationKey, cancellationToken).ConfigureAwait(false)
                ?? throw new FormatException("The reconciliation operation receipt disappeared during inventory validation.");
            var key = GovernedLoopEffectReconciliationArtifactNames.StorageKey(receipt.CaseId);
            var current = cases.FirstOrDefault(value => string.Equals(value.CaseId, receipt.CaseId, StringComparison.Ordinal));
            if (current is null)
            {
                throw new FormatException("The reconciliation operation receipt is not attached to a retained case.");
            }

            var chain = await ReadCaseChainAsync(key, allowMissingHead: false, cancellationToken).ConfigureAwait(false);
            var exact = chain.FirstOrDefault(value => value.CaseVersion == receipt.CaseVersion
                && string.Equals(value.ContentHash, receipt.CaseContentHash, StringComparison.Ordinal));
            if (exact is null || !string.Equals(exact.Binding.ContentHash, receipt.BindingHash, StringComparison.Ordinal))
            {
                throw new FormatException("The reconciliation operation receipt is not attached to its immutable case version.");
            }

            var originalEffect = await ReadAndValidateExpectedEffectAsync(exact, cancellationToken).ConfigureAwait(false);
            var effect = await _effectAttempts.ReadExactForReconciliationAsync(exact.Binding.OperationId, exact.Binding.EffectGeneration, receipt.EffectContentHash!, cancellationToken).ConfigureAwait(false)
                ?? throw new FormatException("The reconciliation operation receipt is not attached to retained effect evidence.");
            if (!string.Equals(effect.ContentHash, originalEffect.ContentHash, StringComparison.Ordinal)
                && !GovernedLoopEffectReconciliationAttemptContract.IsDirectSuccessor(originalEffect, effect, exact))
            {
                throw new FormatException("The reconciliation operation receipt is attached to an unrelated effect-attempt version.");
            }
        }
    }

    private async Task<IReadOnlyList<GovernedLoopEffectReconciliationCase>> ReadCaseChainAsync(
        string storageKey,
        bool allowMissingHead,
        CancellationToken cancellationToken)
    {
        var paths = Directory.EnumerateFiles(_root, GovernedLoopEffectReconciliationPersistenceLimits.CaseFilePrefix + storageKey + ".*.json", SearchOption.TopDirectoryOnly)
            .Select(path => Path.GetFileName(path))
            .Where(name => name is not null && GovernedLoopEffectReconciliationArtifactNames.TryParseCaseVersionFile(name, out var key, out _, out _)
                && string.Equals(key, storageKey, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (paths.Length == 0)
        {
            if (File.Exists(_guard.GetFilePath(_root, GovernedLoopEffectReconciliationArtifactNames.CaseHeadFileName(storageKey))))
            {
                throw new FormatException("The reconciliation case head has no immutable version evidence.");
            }
            return [];
        }
        if (paths.Length > GovernedLoopEffectReconciliationPersistenceLimits.MaximumCaseVersions)
        {
            throw new FormatException("A reconciliation case exceeds its immutable version bound.");
        }

        var versions = new Dictionary<long, GovernedLoopEffectReconciliationCase>();
        foreach (var path in paths)
        {
            if (!GovernedLoopEffectReconciliationArtifactNames.TryParseCaseVersionFile(path!, out var parsedKey, out var version, out var hash)
                || !string.Equals(parsedKey, storageKey, StringComparison.Ordinal))
            {
                throw new FormatException("The reconciliation case version name is malformed.");
            }

            var bytes = await _guard.ReadAllBytesAsync(_root, _guard.GetFilePath(_root, path!), GovernedLoopEffectReconciliationContractLimits.MaxRecordUtf8Bytes, "Reconciliation case", cancellationToken).ConfigureAwait(false);
            if (!GovernedLoopEffectReconciliationRecordCodec.TryDecode(bytes, out var parsed, out _)
                || parsed is null
                || parsed.CaseVersion != version
                || !string.Equals(parsed.ContentHash, hash, StringComparison.Ordinal)
                || !string.Equals(GovernedLoopEffectReconciliationArtifactNames.StorageKey(parsed.CaseId), storageKey, StringComparison.Ordinal)
                || !versions.TryAdd(parsed.CaseVersion, parsed))
            {
                throw new FormatException("The reconciliation case evidence is malformed, noncanonical, or duplicated.");
            }
        }

        var ordered = versions.OrderBy(item => item.Key).Select(item => item.Value).ToArray();
        if (ordered[0].CaseVersion != 1 || ordered[0].PreviousContentHash is not null)
        {
            throw new FormatException("A reconciliation case must contain one version-1 root.");
        }
        for (var index = 1; index < ordered.Length; index++)
        {
            if (!GovernedLoopEffectReconciliationContractValidator.ValidateTransition(ordered[index - 1], ordered[index]).IsValid)
            {
                throw new FormatException("The reconciliation case history is not one contiguous hash chain.");
            }
        }

        var headPath = _guard.GetFilePath(_root, GovernedLoopEffectReconciliationArtifactNames.CaseHeadFileName(storageKey));
        if (!File.Exists(headPath))
        {
            if (!allowMissingHead)
            {
                throw new FormatException("The reconciliation case head is missing and cannot be repaired by a read-only operation.");
            }

            await _guard.WriteTextAtomicallyAsync(_root, headPath, ordered[^1].ContentHash, cancellationToken).ConfigureAwait(false);
            return ordered;
        }

        var head = Encoding.ASCII.GetString(await _guard.ReadAllBytesAsync(_root, headPath, GovernedLoopEffectReconciliationContractLimits.Sha256HexCharacters, "Reconciliation case head", cancellationToken).ConfigureAwait(false));
        if (!IsHash(head) || !string.Equals(head, ordered[^1].ContentHash, StringComparison.Ordinal))
        {
            throw new FormatException("The reconciliation case head is malformed or disconnected from immutable evidence.");
        }

        return ordered;
    }

    private async Task<GovernedLoopEffectAttempt> ReadAndValidateExpectedEffectAsync(
        GovernedLoopEffectReconciliationCase value,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(value.Binding.WorkspaceId, _workspaceId, StringComparison.Ordinal))
        {
            throw new FormatException("The reconciliation case is stored under the wrong workspace binding.");
        }

        var effect = await _effectAttempts.ReadExactForReconciliationAsync(value.Binding.OperationId, value.Binding.EffectGeneration, value.Binding.CurrentAttemptHash, cancellationToken).ConfigureAwait(false)
            ?? throw new FormatException("The reconciliation case does not retain its exact immutable current effect-attempt evidence.");
        if (!GovernedLoopEffectReconciliationContractValidator.Validate(value, effect).IsValid)
        {
            throw new FormatException("The reconciliation case is not bound to its exact immutable effect-attempt evidence.");
        }

        return effect;
    }

    private async Task PublishCaseAsync(GovernedLoopEffectReconciliationCase value, string storageKey, CancellationToken cancellationToken)
    {
        var bytes = GovernedLoopEffectReconciliationRecordCodec.Encode(value);
        var fileName = GovernedLoopEffectReconciliationArtifactNames.CaseVersionFileName(storageKey, value.CaseVersion, value.ContentHash);
        var path = _guard.GetFilePath(_root, fileName);
        if (!await _guard.WriteTextAtomicallyIfAbsentAsync(_root, path, Encoding.UTF8.GetString(bytes), cancellationToken).ConfigureAwait(false))
        {
            var existing = await _guard.ReadAllBytesAsync(_root, path, GovernedLoopEffectReconciliationContractLimits.MaxRecordUtf8Bytes, "Reconciliation case", cancellationToken).ConfigureAwait(false);
            if (!existing.SequenceEqual(bytes))
            {
                throw new FormatException("Immutable reconciliation case evidence conflicted with an existing version.");
            }
        }

        await _guard.WriteTextAtomicallyAsync(
            _root,
            _guard.GetFilePath(_root, GovernedLoopEffectReconciliationArtifactNames.CaseHeadFileName(storageKey)),
            value.ContentHash,
            cancellationToken).ConfigureAwait(false);
    }

    private GovernedLoopEffectReconciliationJournal CreateJournal(GovernedLoopEffectReconciliationCaseMutationRequest request, string operationKey)
    {
        var replacement = Convert.ToBase64String(GovernedLoopEffectReconciliationRecordCodec.Encode(request.Replacement));
        var successor = request.ReconciledEffectSuccessor is null
            ? null
            : Convert.ToBase64String(EmbodySense.Core.Common.Loops.Execution.Effects.GovernedLoopEffectAttemptRecordCodec.Encode(request.ReconciledEffectSuccessor));
        return new GovernedLoopEffectReconciliationJournal(
            GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
            request.OperationId,
            request.RequestHash,
            request.Purpose,
            request.Replacement.CaseId,
            GovernedLoopEffectReconciliationArtifactNames.StorageKey(request.Replacement.CaseId),
            replacement,
            successor,
            request.Replacement.ContentHash,
            request.Replacement.CaseVersion,
            request.ExpectedCaseContentHash,
            request.Binding.CurrentAttemptHash,
            GovernedLoopEffectReconciliationJournalStage.Pending,
            _timeProvider.GetUtcNow(),
            string.Empty);
    }

    private static GovernedLoopEffectReconciliationOperationReceipt CreateReceipt(
        GovernedLoopEffectReconciliationCaseMutationRequest request,
        string effectContentHash,
        DateTimeOffset committedAtUtc)
        => GovernedLoopEffectReconciliationReceiptCodec.Materialize(new(
            GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
            request.OperationId,
            request.RequestHash,
            request.Purpose,
            request.Replacement.CaseId,
            request.Replacement.CaseVersion,
            request.Replacement.ContentHash,
            request.Replacement.Binding.ContentHash,
            GovernedLoopEffectReconciliationCaseMutationStatus.Applied,
            effectContentHash,
            committedAtUtc,
            string.Empty));

    private static GovernedLoopEffectReconciliationOperationReceipt CreateReceiptFromJournal(GovernedLoopEffectReconciliationJournal journal, string effectContentHash)
        => GovernedLoopEffectReconciliationReceiptCodec.Materialize(new(
            GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
            journal.OperationId,
            journal.RequestHash,
            journal.Purpose,
            journal.CaseId,
            journal.ReplacementVersion,
            journal.ReplacementHash,
            DecodeCase(journal.ReplacementJson).Binding.ContentHash,
            GovernedLoopEffectReconciliationCaseMutationStatus.Applied,
            effectContentHash,
            journal.CreatedAtUtc,
            string.Empty));

    private async Task WriteJournalIfAbsentAsync(string operationKey, GovernedLoopEffectReconciliationJournal journal, CancellationToken cancellationToken)
    {
        var path = _guard.GetFilePath(_root, GovernedLoopEffectReconciliationArtifactNames.JournalFileName(operationKey));
        var bytes = GovernedLoopEffectReconciliationJournalCodec.Encode(journal);
        if (!await _guard.WriteTextAtomicallyIfAbsentAsync(_root, path, Encoding.UTF8.GetString(bytes), cancellationToken).ConfigureAwait(false))
        {
            var existing = await _guard.ReadAllBytesAsync(_root, path, GovernedLoopEffectReconciliationPersistenceLimits.MaximumJournalUtf8Bytes, "Reconciliation transaction journal", cancellationToken).ConfigureAwait(false);
            if (!existing.SequenceEqual(bytes))
            {
                throw new GovernedLoopEffectReconciliationRepairRequiredException("A reconciliation operation already has a different interrupted transaction journal.");
            }
        }
    }

    private async Task WriteJournalAsync(string operationKey, GovernedLoopEffectReconciliationJournal journal, CancellationToken cancellationToken)
    {
        var bytes = GovernedLoopEffectReconciliationJournalCodec.Encode(journal);
        await _guard.WriteTextAtomicallyAsync(
            _root,
            _guard.GetFilePath(_root, GovernedLoopEffectReconciliationArtifactNames.JournalFileName(operationKey)),
            Encoding.UTF8.GetString(bytes),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteReceiptIfAbsentAsync(string operationKey, GovernedLoopEffectReconciliationOperationReceipt receipt, CancellationToken cancellationToken)
    {
        var bytes = GovernedLoopEffectReconciliationReceiptCodec.Encode(receipt);
        var path = _guard.GetFilePath(_root, GovernedLoopEffectReconciliationArtifactNames.ReceiptFileName(operationKey));
        if (!await _guard.WriteTextAtomicallyIfAbsentAsync(_root, path, Encoding.UTF8.GetString(bytes), cancellationToken).ConfigureAwait(false))
        {
            var existing = await _guard.ReadAllBytesAsync(_root, path, GovernedLoopEffectReconciliationPersistenceLimits.MaximumReceiptUtf8Bytes, "Reconciliation operation receipt", cancellationToken).ConfigureAwait(false);
            if (!GovernedLoopEffectReconciliationReceiptCodec.TryDecode(existing, out var decoded)
                || decoded is null
                || !SameReceipt(receipt, decoded))
            {
                throw new GovernedLoopEffectReconciliationRepairRequiredException("A reconciliation operation receipt conflicted with immutable evidence.");
            }
        }
    }

    private async Task<GovernedLoopEffectReconciliationOperationReceipt?> ReadReceiptAsync(string operationKey, CancellationToken cancellationToken)
    {
        var path = _guard.GetFilePath(_root, GovernedLoopEffectReconciliationArtifactNames.ReceiptFileName(operationKey));
        if (!File.Exists(path))
        {
            return null;
        }

        var bytes = await _guard.ReadAllBytesAsync(_root, path, GovernedLoopEffectReconciliationPersistenceLimits.MaximumReceiptUtf8Bytes, "Reconciliation operation receipt", cancellationToken).ConfigureAwait(false);
        if (!GovernedLoopEffectReconciliationReceiptCodec.TryDecode(bytes, out var receipt) || receipt is null
            || receipt.SchemaVersion != GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion
            || !CustomLoopArtifactIdentifier.IsValid(receipt.OperationId, GovernedLoopEffectReconciliationContractLimits.MaxIdentifierCharacters)
            || !string.Equals(GovernedLoopEffectReconciliationArtifactNames.OperationKey(receipt.OperationId), operationKey, StringComparison.Ordinal)
            || !CustomLoopArtifactIdentifier.IsValid(receipt.Purpose, GovernedLoopEffectReconciliationContractLimits.MaxIdentifierCharacters)
            || !CustomLoopArtifactIdentifier.IsValid(receipt.CaseId, GovernedLoopEffectReconciliationContractLimits.MaxIdentifierCharacters)
            || !IsHash(receipt.RequestHash)
            || !IsHash(receipt.CaseContentHash)
            || !IsHash(receipt.BindingHash)
            || receipt.EffectContentHash is null
            || !IsHash(receipt.EffectContentHash)
            || receipt.CommittedAtUtc == default
            || receipt.CommittedAtUtc.Offset != TimeSpan.Zero
            || receipt.Status != GovernedLoopEffectReconciliationCaseMutationStatus.Applied)
        {
            throw new FormatException("The reconciliation operation receipt is malformed or not bound to its operation identity.");
        }

        return receipt;
    }

    private async Task<GovernedLoopEffectReconciliationJournal?> ReadJournalAsync(string operationKey, CancellationToken cancellationToken)
    {
        var path = _guard.GetFilePath(_root, GovernedLoopEffectReconciliationArtifactNames.JournalFileName(operationKey));
        if (!File.Exists(path))
        {
            return null;
        }

        var bytes = await _guard.ReadAllBytesAsync(_root, path, GovernedLoopEffectReconciliationPersistenceLimits.MaximumJournalUtf8Bytes, "Reconciliation transaction journal", cancellationToken).ConfigureAwait(false);
        if (!GovernedLoopEffectReconciliationJournalCodec.TryDecode(bytes, out var journal) || journal is null
            || journal.SchemaVersion != GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion
            || !CustomLoopArtifactIdentifier.IsValid(journal.OperationId, GovernedLoopEffectReconciliationContractLimits.MaxIdentifierCharacters)
            || !string.Equals(GovernedLoopEffectReconciliationArtifactNames.OperationKey(journal.OperationId), operationKey, StringComparison.Ordinal)
            || !CustomLoopArtifactIdentifier.IsValid(journal.Purpose, GovernedLoopEffectReconciliationContractLimits.MaxIdentifierCharacters)
            || !CustomLoopArtifactIdentifier.IsValid(journal.CaseId, GovernedLoopEffectReconciliationContractLimits.MaxIdentifierCharacters)
            || !string.Equals(GovernedLoopEffectReconciliationArtifactNames.StorageKey(journal.CaseId), journal.StorageKey, StringComparison.Ordinal)
            || !IsHash(journal.RequestHash)
            || !IsHash(journal.StorageKey)
            || !IsHash(journal.ReplacementHash)
            || journal.ExpectedEffectHash is null
            || !IsHash(journal.ExpectedEffectHash)
            || journal.ExpectedCaseHash is not null && !IsHash(journal.ExpectedCaseHash)
            || !Enum.IsDefined(journal.Stage)
            || journal.CreatedAtUtc == default
            || journal.CreatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new FormatException("The reconciliation transaction journal is malformed or not bound to its operation identity.");
        }

        return journal;
    }

    private bool HasPendingJournal()
        => Directory.Exists(_root)
            && Directory.EnumerateFiles(_root, GovernedLoopEffectReconciliationPersistenceLimits.JournalFilePrefix + "*.json", SearchOption.TopDirectoryOnly)
                .Any(path => GovernedLoopEffectReconciliationArtifactNames.TryParseJournalFile(Path.GetFileName(path), out _));

    private void Observe(GovernedLoopEffectReconciliationPersistenceBoundary boundary)
        => _options.DurableBoundaryObserver?.Invoke(boundary);

    private async Task ValidatePendingJournalsAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(_root, GovernedLoopEffectReconciliationPersistenceLimits.JournalFilePrefix + "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GovernedLoopEffectReconciliationArtifactNames.TryParseJournalFile(Path.GetFileName(path), out var operationKey))
            {
                var journal = await ReadJournalAsync(operationKey, cancellationToken).ConfigureAwait(false)
                    ?? throw new FormatException("The reconciliation transaction journal disappeared during validation.");
                ValidatePendingJournalPayload(journal);
            }
        }
    }

    private static void ValidatePendingJournalPayload(GovernedLoopEffectReconciliationJournal journal)
    {
        var replacement = DecodeCase(journal.ReplacementJson);
        if (!GovernedLoopEffectReconciliationContractValidator.Validate(replacement).IsValid
            || !string.Equals(replacement.CaseId, journal.CaseId, StringComparison.Ordinal)
            || !string.Equals(GovernedLoopEffectReconciliationArtifactNames.StorageKey(replacement.CaseId), journal.StorageKey, StringComparison.Ordinal)
            || !string.Equals(replacement.ContentHash, journal.ReplacementHash, StringComparison.Ordinal)
            || replacement.CaseVersion != journal.ReplacementVersion
            || !string.Equals(replacement.Binding.OperationId, journal.OperationId, StringComparison.Ordinal)
            || !string.Equals(replacement.Binding.CurrentAttemptHash, journal.ExpectedEffectHash, StringComparison.Ordinal))
        {
            throw new FormatException("The reconciliation journal is not bound to a canonical immutable case payload.");
        }

        if (journal.ExpectedCaseHash is null)
        {
            if (replacement.CaseVersion != 1 || replacement.PreviousContentHash is not null)
            {
                throw new FormatException("The reconciliation journal create payload is not a version-one root.");
            }
        }
        else if (replacement.CaseVersion <= 1 || !string.Equals(replacement.PreviousContentHash, journal.ExpectedCaseHash, StringComparison.Ordinal))
        {
            throw new FormatException("The reconciliation journal update payload is not the direct expected case successor.");
        }

        if (journal.SuccessorJson is null)
        {
            if (journal.Stage == GovernedLoopEffectReconciliationJournalStage.EffectPublished)
            {
                throw new FormatException("The reconciliation journal marks an effect publication without an effect successor.");
            }
            return;
        }

        var successor = DecodeAttempt(journal.SuccessorJson);
        if (GovernedLoopEffectAttemptContract.Validate(successor) is not null
            || successor.Payload.Phase != GovernedLoopEffectPhase.Reconciled
            || !Equals(successor.Binding, replacement.Binding.Execution)
            || !string.Equals(successor.NodeId, replacement.Binding.NodeId, StringComparison.Ordinal)
            || successor.NodeAttempt != replacement.Binding.NodeAttempt
            || !string.Equals(successor.Payload.EffectId, replacement.Binding.EffectId, StringComparison.Ordinal)
            || !string.Equals(successor.Payload.OperationId, replacement.Binding.OperationId, StringComparison.Ordinal)
            || successor.Payload.EffectGeneration != replacement.Binding.EffectGeneration
            || !string.Equals(successor.Payload.IntentHash, replacement.Binding.IntentHash, StringComparison.Ordinal))
        {
            throw new FormatException("The reconciliation journal effect successor is not canonically bound to its case payload.");
        }
    }

    private void DeleteJournal(string operationKey)
        => _guard.DeleteFile(_root, _guard.GetFilePath(_root, GovernedLoopEffectReconciliationArtifactNames.JournalFileName(operationKey)));

    private int CountCaseHeads()
        => Directory.Exists(_root)
            ? Directory.EnumerateFiles(_root, GovernedLoopEffectReconciliationPersistenceLimits.CaseFilePrefix + "*.head", SearchOption.TopDirectoryOnly)
                .Count(path => GovernedLoopEffectReconciliationArtifactNames.TryParseCaseHeadFile(Path.GetFileName(path), out _))
            : 0;

    private int CountReceiptFiles()
        => Directory.Exists(_root)
            ? Directory.EnumerateFiles(_root, GovernedLoopEffectReconciliationPersistenceLimits.ReceiptFilePrefix + "*.json", SearchOption.TopDirectoryOnly)
                .Count(path => GovernedLoopEffectReconciliationArtifactNames.TryParseReceiptFile(Path.GetFileName(path), out _))
            : 0;

    private async Task<FileStream> AcquireExistingReadLockAsync(CancellationToken cancellationToken)
    {
        var lockPath = _guard.GetFilePath(_root, ".custom-loop-mutations.lock");
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.None, bufferSize: 1, FileOptions.WriteThrough);
            }
            catch (FileNotFoundException)
            {
                throw new FormatException("The canonical effect-attempt mutation lock is missing from a populated artifact root.");
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Yield();
            }
        }

        throw new IOException("The canonical effect-attempt artifact remained locked after bounded reconciliation read retries.");
    }

    private static GovernedLoopEffectReconciliationCaseSummary ToSummary(GovernedLoopEffectReconciliationCase value)
        => new(value.CaseId, value.CaseVersion, value.ContentHash, value.Binding.ContentHash, value.Resolution is not null
            ? GovernedLoopEffectReconciliationCaseSummaryStatus.Resolved
            : value.Disposition?.Kind == GovernedLoopEffectReconciliationDispositionKind.QuarantineUnresolved
                ? GovernedLoopEffectReconciliationCaseSummaryStatus.Quarantined
                : value.Disposition is not null
                    ? GovernedLoopEffectReconciliationCaseSummaryStatus.Accepted
                    : value.CurrentAssessmentHash is not null
                        ? GovernedLoopEffectReconciliationCaseSummaryStatus.Assessed
                        : GovernedLoopEffectReconciliationCaseSummaryStatus.Open);

    private static GovernedLoopEffectReconciliationCaseListPage ReadyPage(
        IReadOnlyList<GovernedLoopEffectReconciliationCaseSummary> values,
        string? nextCursor)
        => new(GovernedLoopEffectReconciliationCaseListStatus.Ready, values, nextCursor);

    private static GovernedLoopEffectReconciliationCaseMutationResult CurrentResult(
        GovernedLoopEffectReconciliationCaseMutationStatus status,
        GovernedLoopEffectReconciliationCase value,
        GovernedLoopEffectAttempt effect)
        => new(status, value, effect);

    private static GovernedLoopEffectReconciliationCaseMutationResult MutationResult(GovernedLoopEffectReconciliationCaseMutationStatus status)
        => new(status, null, null);

    private static bool SameReceiptRequest(
        GovernedLoopEffectReconciliationOperationReceipt receipt,
        GovernedLoopEffectReconciliationCaseMutationRequest request)
        => string.Equals(receipt.OperationId, request.OperationId, StringComparison.Ordinal)
            && string.Equals(receipt.RequestHash, request.RequestHash, StringComparison.Ordinal)
            && string.Equals(receipt.Purpose, request.Purpose, StringComparison.Ordinal)
            && string.Equals(receipt.CaseId, request.Replacement.CaseId, StringComparison.Ordinal);

    private static bool SameReceipt(
        GovernedLoopEffectReconciliationOperationReceipt left,
        GovernedLoopEffectReconciliationOperationReceipt right)
        => left == right;

    private static string CreateCursor(string caseId)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes("v1\n" + caseId)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryReadCursor(string? cursor, out string? caseId)
    {
        caseId = null;
        if (cursor is null)
        {
            return true;
        }
        if (cursor.Length > GovernedLoopEffectReconciliationPersistenceLimits.MaximumCursorBytes || cursor.Length == 0)
        {
            return false;
        }

        try
        {
            var normalized = cursor.Replace('-', '+').Replace('_', '/');
            normalized += new string('=', (4 - normalized.Length % 4) % 4);
            var value = Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
            if (!value.StartsWith("v1\n", StringComparison.Ordinal))
            {
                return false;
            }
            var candidate = value[3..];
            if (!CustomLoopArtifactIdentifier.IsValid(candidate, GovernedLoopEffectReconciliationContractLimits.MaxIdentifierCharacters))
            {
                return false;
            }
            if (!string.Equals(CreateCursor(candidate), cursor, StringComparison.Ordinal))
            {
                return false;
            }
            caseId = candidate;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static GovernedLoopEffectReconciliationCase DecodeCase(string encoded)
    {
        try
        {
            var bytes = Convert.FromBase64String(encoded);
            if (!GovernedLoopEffectReconciliationRecordCodec.TryDecode(bytes, out var value, out _) || value is null)
            {
                throw new FormatException("The reconciliation journal case payload is malformed.");
            }
            return value;
        }
        catch (FormatException exception)
        {
            throw new FormatException("The reconciliation journal case payload is malformed.", exception);
        }
    }

    private static GovernedLoopEffectAttempt DecodeAttempt(string encoded)
    {
        try
        {
            var bytes = Convert.FromBase64String(encoded);
            if (!EmbodySense.Core.Common.Loops.Execution.Effects.GovernedLoopEffectAttemptRecordCodec.TryDecode(bytes, out var value, out _) || value is null)
            {
                throw new FormatException("The reconciliation journal effect payload is malformed.");
            }
            return value;
        }
        catch (FormatException exception)
        {
            throw new FormatException("The reconciliation journal effect payload is malformed.", exception);
        }
    }

    private static bool IsHash(string? value)
        => value is { Length: GovernedLoopEffectReconciliationContractLimits.Sha256HexCharacters }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsCorrupt(Exception exception)
        => exception is FormatException or InvalidDataException or OverflowException or ArgumentException or GovernedLoopEffectReconciliationRepairRequiredException;

    private static bool IsUnavailable(Exception exception)
        => exception is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or NotSupportedException or PlatformNotSupportedException;
}
