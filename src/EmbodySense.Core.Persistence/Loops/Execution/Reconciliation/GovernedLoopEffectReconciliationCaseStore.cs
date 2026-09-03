using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
using EmbodySense.Core.Persistence.Loops.Admission;
using EmbodySense.Core.Persistence.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Persistence.Loops.Execution.Reconciliation;

/// <summary>Persists canonical reconciliation cases in the existing effect-attempt artifact envelope.</summary>
/// <remarks>
/// Case versions, operation receipts, and the small recovery journal live beside effect-attempt versions under the
/// existing effect-attempt root. All mutations run through the supplied effect-attempt store so the same cross-process
/// mutation lease protects both the case and any proof-backed reconciled effect successor. No separate reconciliation
/// directory, lock, or authority ledger is created.
/// </remarks>
public sealed class GovernedLoopEffectReconciliationCaseStore : IGovernedLoopEffectReconciliationCaseStore, IGovernedLoopEffectReconciliationResolutionReader, IGovernedLoopEffectReconciliationProbeReservationStore
{
    private static readonly JsonSerializerOptions _probeJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
        MaxDepth = 32,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false), new GovernedLoopExecutionBindingJsonConverter() }
    };
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
            await ValidateProbeInventoryAsync(cases, cancellationToken).ConfigureAwait(false);
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
                    await ValidateProbeInventoryAsync(cases, token).ConfigureAwait(false);
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
            await ValidateProbeInventoryAsync(persistedCases, cancellationToken).ConfigureAwait(false);
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
            await ValidateProbeInventoryAsync(allCases, cancellationToken).ConfigureAwait(false);
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
    public async Task<GovernedLoopEffectReconciliationProbeReservationResult> ReserveAsync(
        GovernedLoopEffectReconciliationProbeReservationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await _effectAttempts.ExecuteReconciliationMutationAsync(
                (_, token) => ReserveProbeUnderLockAsync(request, token),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (GovernedLoopEffectReconciliationRepairRequiredException)
        {
            return ProbeReservationResult(GovernedLoopEffectReconciliationProbeReservationStatus.RepairRequired);
        }
        catch (Exception exception) when (IsCorrupt(exception))
        {
            return ProbeReservationResult(GovernedLoopEffectReconciliationProbeReservationStatus.Corrupt);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return ProbeReservationResult(GovernedLoopEffectReconciliationProbeReservationStatus.Unavailable);
        }
    }

    /// <inheritdoc />
    public async Task<GovernedLoopEffectReconciliationProbeReservationStatus> ValidateBeforeCallbackAsync(
        GovernedLoopEffectReconciliationProbeReservation reservation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await _effectAttempts.ExecuteReconciliationMutationAsync(
                (_, token) => ValidateProbeReservationHeadUnderLockAsync(reservation, token),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (GovernedLoopEffectReconciliationRepairRequiredException)
        {
            return GovernedLoopEffectReconciliationProbeReservationStatus.RepairRequired;
        }
        catch (Exception exception) when (IsCorrupt(exception))
        {
            return GovernedLoopEffectReconciliationProbeReservationStatus.Corrupt;
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return GovernedLoopEffectReconciliationProbeReservationStatus.Unavailable;
        }
    }

    private async Task<GovernedLoopEffectReconciliationProbeReservationStatus> ValidateProbeReservationHeadUnderLockAsync(
        GovernedLoopEffectReconciliationProbeReservation reservation,
        CancellationToken cancellationToken)
    {
        var operationKey = GovernedLoopEffectReconciliationArtifactNames.OperationKey(reservation.OperationId);
        var stored = await ReadProbeReservationAsync(operationKey, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return GovernedLoopEffectReconciliationProbeReservationStatus.Unavailable;
        }
        if (!SameProbeReservation(stored, reservation))
        {
            return GovernedLoopEffectReconciliationProbeReservationStatus.Conflict;
        }

        var chain = await ReadCaseChainAsync(GovernedLoopEffectReconciliationArtifactNames.StorageKey(reservation.Context.Case.CaseId), allowMissingHead: false, cancellationToken).ConfigureAwait(false);
        var current = chain.LastOrDefault();
        var effect = await _effectAttempts.ReadCurrentForReconciliationAsync(reservation.Context.Binding.OperationId, reservation.Context.Binding.EffectGeneration, cancellationToken).ConfigureAwait(false);
        return current is not null
            && current.CaseVersion == reservation.Context.Case.CaseVersion
            && string.Equals(current.ContentHash, reservation.Context.Case.ContentHash, StringComparison.Ordinal)
            && string.Equals(current.Binding.ContentHash, reservation.Context.Binding.ContentHash, StringComparison.Ordinal)
            && current.Disposition is null
            && current.Resolution is null
            && effect is not null
            && string.Equals(effect.ContentHash, reservation.Context.EffectHead.ContentHash, StringComparison.Ordinal)
            && GovernedLoopEffectReconciliationContractValidator.Validate(current, effect).IsValid
            ? GovernedLoopEffectReconciliationProbeReservationStatus.Reserved
            : GovernedLoopEffectReconciliationProbeReservationStatus.Conflict;
    }

    /// <inheritdoc />
    public async Task<GovernedLoopEffectReconciliationProbeObservationCommitResult> CommitObservationAsync(
        GovernedLoopEffectReconciliationProbeObservationCommitRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await _effectAttempts.ExecuteReconciliationMutationAsync(
                (_, token) => CommitProbeObservationUnderLockAsync(request, token),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (GovernedLoopEffectReconciliationRepairRequiredException)
        {
            return ProbeCommitResult(GovernedLoopEffectReconciliationProbeReservationStatus.RepairRequired);
        }
        catch (Exception exception) when (IsCorrupt(exception))
        {
            return ProbeCommitResult(GovernedLoopEffectReconciliationProbeReservationStatus.Corrupt);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return ProbeCommitResult(GovernedLoopEffectReconciliationProbeReservationStatus.Unavailable);
        }
    }

    private async Task<GovernedLoopEffectReconciliationProbeReservationResult> ReserveProbeUnderLockAsync(
        GovernedLoopEffectReconciliationProbeReservationRequest request,
        CancellationToken cancellationToken)
    {
        await RecoverPendingJournalsUnderLockAsync(cancellationToken).ConfigureAwait(false);
        var allCases = await ReadAllCurrentCasesAsync(cancellationToken).ConfigureAwait(false);
        await ValidateReceiptInventoryAsync(allCases, cancellationToken).ConfigureAwait(false);
        await ValidateProbeInventoryAsync(allCases, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(request.Context.Case.BindingHash, request.Context.Binding.ContentHash, StringComparison.Ordinal)
            || !string.Equals(request.Context.EffectHead.ContentHash, request.Context.Binding.CurrentAttemptHash, StringComparison.Ordinal)
            || !string.Equals(request.Context.InputFingerprint, request.Context.EffectHead.InputFingerprint, StringComparison.Ordinal))
        {
            return ProbeReservationResult(GovernedLoopEffectReconciliationProbeReservationStatus.Invalid);
        }

        var operationKey = GovernedLoopEffectReconciliationArtifactNames.OperationKey(request.OperationId);
        var observation = await ReadProbeObservationAsync(operationKey, cancellationToken).ConfigureAwait(false);
        if (observation is not null)
        {
            if (!string.Equals(observation.RequestHash, request.RequestHash, StringComparison.Ordinal))
            {
                return ProbeReservationResult(GovernedLoopEffectReconciliationProbeReservationStatus.Conflict);
            }

            var storedReservation = await ReadProbeReservationAsync(operationKey, cancellationToken).ConfigureAwait(false)
                ?? throw new GovernedLoopEffectReconciliationRepairRequiredException("A probe observation is missing its durable reservation.");
            var completed = await ReadProbeCommitPayloadAsync(observation, storedReservation, cancellationToken).ConfigureAwait(false);
            return new GovernedLoopEffectReconciliationProbeReservationResult(GovernedLoopEffectReconciliationProbeReservationStatus.Replayed, storedReservation, completed.Case, completed.EffectHead);
        }

        var existing = await ReadProbeReservationAsync(operationKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return string.Equals(existing.RequestHash, request.RequestHash, StringComparison.Ordinal)
                ? new GovernedLoopEffectReconciliationProbeReservationResult(GovernedLoopEffectReconciliationProbeReservationStatus.Replayed, existing)
                : ProbeReservationResult(GovernedLoopEffectReconciliationProbeReservationStatus.Conflict);
        }

        if (await HasIncompleteProbeReservationForCaseAsync(request.Context.Case, cancellationToken).ConfigureAwait(false))
        {
            return ProbeReservationResult(GovernedLoopEffectReconciliationProbeReservationStatus.Conflict);
        }

        if (CountProbeReservationFiles() >= GovernedLoopEffectReconciliationPersistenceLimits.MaximumProbeReservations
            || await CountProbeReservationsForCaseAsync(request.Context.Case.CaseId, cancellationToken).ConfigureAwait(false) >= GovernedLoopEffectReconciliationPersistenceLimits.MaximumProbeReservationsPerCase)
        {
            return ProbeReservationResult(GovernedLoopEffectReconciliationProbeReservationStatus.CapacityExceeded);
        }

        var key = GovernedLoopEffectReconciliationArtifactNames.StorageKey(request.Context.Case.CaseId);
        var chain = await ReadCaseChainAsync(key, allowMissingHead: false, cancellationToken).ConfigureAwait(false);
        var currentCase = chain.LastOrDefault();
        var currentEffect = await _effectAttempts.ReadCurrentForReconciliationAsync(request.Context.Binding.OperationId, request.Context.Binding.EffectGeneration, cancellationToken).ConfigureAwait(false);
        if (currentCase is null || currentEffect is null
            || currentCase.CaseVersion != request.Context.Case.CaseVersion
            || !string.Equals(currentCase.ContentHash, request.Context.Case.ContentHash, StringComparison.Ordinal)
            || !string.Equals(currentCase.Binding.ContentHash, request.Context.Binding.ContentHash, StringComparison.Ordinal))
        {
            return ProbeReservationResult(GovernedLoopEffectReconciliationProbeReservationStatus.Conflict);
        }
        if (currentCase.Disposition is not null || currentCase.Resolution is not null
            || !GovernedLoopEffectReconciliationContractValidator.Validate(currentCase, currentEffect).IsValid
            || !string.Equals(currentEffect.ContentHash, request.Context.EffectHead.ContentHash, StringComparison.Ordinal))
        {
            return ProbeReservationResult(GovernedLoopEffectReconciliationProbeReservationStatus.Conflict);
        }

        var now = _timeProvider.GetUtcNow();
        if (now.Offset != TimeSpan.Zero || now == default)
        {
            return ProbeReservationResult(GovernedLoopEffectReconciliationProbeReservationStatus.Unavailable);
        }
        var reservation = new GovernedLoopEffectReconciliationProbeReservation(
            request.OperationId,
            request.RequestHash,
            CreateProbeInvocationId(),
            request.Context,
            now);
        var artifact = CreateReservationArtifact(reservation);
        if (!HasProbeRetainedCapacity(artifact, null, null, null, includeReservationBudget: true))
        {
            return ProbeReservationResult(GovernedLoopEffectReconciliationProbeReservationStatus.CapacityExceeded);
        }
        await WriteProbeReservationAsync(operationKey, artifact, cancellationToken).ConfigureAwait(false);
        Observe(GovernedLoopEffectReconciliationPersistenceBoundary.ProbeReservationPublished);
        return new GovernedLoopEffectReconciliationProbeReservationResult(GovernedLoopEffectReconciliationProbeReservationStatus.Reserved, reservation);
    }

    private async Task<GovernedLoopEffectReconciliationProbeObservationCommitResult> CommitProbeObservationUnderLockAsync(
        GovernedLoopEffectReconciliationProbeObservationCommitRequest request,
        CancellationToken cancellationToken)
    {
        await RecoverPendingJournalsUnderLockAsync(cancellationToken).ConfigureAwait(false);
        var allCases = await ReadAllCurrentCasesAsync(cancellationToken).ConfigureAwait(false);
        await ValidateReceiptInventoryAsync(allCases, cancellationToken).ConfigureAwait(false);
        await ValidateProbeInventoryAsync(allCases, cancellationToken).ConfigureAwait(false);
        var operationKey = GovernedLoopEffectReconciliationArtifactNames.OperationKey(request.Reservation.OperationId);
        var storedReservation = await ReadProbeReservationAsync(operationKey, cancellationToken).ConfigureAwait(false);
        if (storedReservation is null)
        {
            return ProbeCommitResult(GovernedLoopEffectReconciliationProbeReservationStatus.Unavailable);
        }
        if (!SameProbeReservation(storedReservation, request.Reservation))
        {
            return ProbeCommitResult(GovernedLoopEffectReconciliationProbeReservationStatus.Conflict);
        }

        var existing = await ReadProbeObservationAsync(operationKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            var completed = await ReadProbeCommitPayloadAsync(existing, storedReservation, cancellationToken).ConfigureAwait(false);
            return new GovernedLoopEffectReconciliationProbeObservationCommitResult(GovernedLoopEffectReconciliationProbeReservationStatus.Replayed, completed.Case, completed.EffectHead);
        }

        var key = GovernedLoopEffectReconciliationArtifactNames.StorageKey(storedReservation.Context.Case.CaseId);
        var chain = await ReadCaseChainAsync(key, allowMissingHead: false, cancellationToken).ConfigureAwait(false);
        var currentCase = chain.LastOrDefault();
        var currentEffect = await _effectAttempts.ReadCurrentForReconciliationAsync(storedReservation.Context.EffectHead.Payload.OperationId, storedReservation.Context.EffectHead.Payload.EffectGeneration, cancellationToken).ConfigureAwait(false);
        if (currentCase is null || currentEffect is null || !string.Equals(currentCase.ContentHash, storedReservation.Context.Case.ContentHash, StringComparison.Ordinal)
            || !string.Equals(currentEffect.ContentHash, storedReservation.Context.EffectHead.ContentHash, StringComparison.Ordinal))
        {
            return ProbeCommitResult(GovernedLoopEffectReconciliationProbeReservationStatus.Conflict);
        }

        var now = _timeProvider.GetUtcNow();
        if (now == default || now.Offset != TimeSpan.Zero || now < currentCase.UpdatedAtUtc || now < storedReservation.ReservedAtUtc)
        {
            return ProbeCommitResult(GovernedLoopEffectReconciliationProbeReservationStatus.Unavailable);
        }
        var observation = NormalizeProbeObservation(request.Result, storedReservation, now);
        if (currentCase.ObservationHistory.Any(item => string.Equals(item.ObservationId, observation.ObservationId, StringComparison.Ordinal)))
        {
            return ProbeCommitResult(GovernedLoopEffectReconciliationProbeReservationStatus.Conflict);
        }
        if (currentCase.ObservationHistory.Count >= GovernedLoopEffectReconciliationContractLimits.MaxObservations
            || CountProbeObservationFiles() >= GovernedLoopEffectReconciliationPersistenceLimits.MaximumProbeObservations)
        {
            return ProbeCommitResult(GovernedLoopEffectReconciliationProbeReservationStatus.CapacityExceeded);
        }
        var next = CreateProbeResultCase(currentCase, observation, now);
        if (!GovernedLoopEffectReconciliationContractValidator.ValidateTransition(currentCase, next).IsValid)
        {
            return ProbeCommitResult(GovernedLoopEffectReconciliationProbeReservationStatus.Invalid);
        }

        var result = new GovernedLoopEffectReconciliationProbeObservationArtifact(
            GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
            storedReservation.OperationId,
            storedReservation.RequestHash,
            storedReservation.Context.Case.CaseId,
            storedReservation.Context.Case.CaseVersion,
            storedReservation.Context.Case.ContentHash,
            storedReservation.Context.Case.BindingHash,
            storedReservation.Context.EffectHead.ContentHash,
            request.Result.Status,
            JsonSerializer.Serialize(observation, _probeJson),
            next.CaseVersion,
            next.ContentHash,
            now,
            string.Empty);
        var reservationArtifact = CreateReservationArtifact(storedReservation);
        var journal = new GovernedLoopEffectReconciliationProbeJournal(
            GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
            storedReservation.OperationId,
            storedReservation.RequestHash,
            Encoding.UTF8.GetString(GovernedLoopEffectReconciliationProbeArtifactCodec.EncodeReservation(reservationArtifact)),
            Encoding.UTF8.GetString(GovernedLoopEffectReconciliationProbeArtifactCodec.EncodeObservation(result)),
            GovernedLoopEffectReconciliationProbeJournalStage.Pending,
            now,
            string.Empty);
        if (!HasProbeRetainedCapacity(null, result, journal, next, includeReservationBudget: false, excludeCurrentReservationBudget: true))
        {
            return ProbeCommitResult(GovernedLoopEffectReconciliationProbeReservationStatus.CapacityExceeded);
        }
        await WriteProbeJournalIfAbsentAsync(operationKey, journal, cancellationToken).ConfigureAwait(false);
        await WriteProbeObservationAsync(operationKey, result, cancellationToken).ConfigureAwait(false);
        Observe(GovernedLoopEffectReconciliationPersistenceBoundary.ProbeObservationPublished);
        journal = journal with { Stage = GovernedLoopEffectReconciliationProbeJournalStage.ObservationPublished, ContentHash = string.Empty };
        await WriteProbeJournalAsync(operationKey, journal, cancellationToken).ConfigureAwait(false);
        await PublishCaseAsync(next, key, cancellationToken).ConfigureAwait(false);
        Observe(GovernedLoopEffectReconciliationPersistenceBoundary.ProbeCasePublished);
        DeleteProbeJournal(operationKey);
        Observe(GovernedLoopEffectReconciliationPersistenceBoundary.ProbeReceiptPublished);
        return new GovernedLoopEffectReconciliationProbeObservationCommitResult(GovernedLoopEffectReconciliationProbeReservationStatus.Reserved, next, currentEffect);
    }

    private GovernedLoopEffectReconciliationProbeReservationArtifact CreateReservationArtifact(
        GovernedLoopEffectReconciliationProbeReservation reservation)
    {
        return new GovernedLoopEffectReconciliationProbeReservationArtifact(
            GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
            reservation.OperationId,
            reservation.RequestHash,
            reservation.ProbeInvocationId,
            reservation.Context.Case.CaseId,
            reservation.Context.Case.CaseVersion,
            reservation.Context.Case.ContentHash,
            reservation.Context.Case.BindingHash,
            JsonSerializer.Serialize(reservation.Context.Binding, _probeJson),
            reservation.Context.EffectHead.ContentHash,
            Convert.ToBase64String(EmbodySense.Core.Common.Loops.Execution.Effects.GovernedLoopEffectAttemptRecordCodec.Encode(reservation.Context.EffectHead)),
            JsonSerializer.Serialize(reservation.Context.Source, _probeJson),
            JsonSerializer.Serialize(reservation.Context.Contract, _probeJson),
            JsonSerializer.Serialize(reservation.Context.Target, _probeJson),
            reservation.Context.InputFingerprint,
            reservation.ReservedAtUtc,
            string.Empty);
    }

    private async Task<GovernedLoopEffectReconciliationProbeReservation?> ReadProbeReservationAsync(string operationKey, CancellationToken cancellationToken)
    {
        var path = _guard.GetFilePath(_root, GovernedLoopEffectReconciliationArtifactNames.ProbeReservationFileName(operationKey));
        if (!File.Exists(path))
        {
            return null;
        }

        var bytes = await _guard.ReadAllBytesAsync(_root, path, GovernedLoopEffectReconciliationPersistenceLimits.MaximumProbeReservationUtf8Bytes, "Reconciliation probe reservation", cancellationToken).ConfigureAwait(false);
        if (!GovernedLoopEffectReconciliationProbeArtifactCodec.TryDecodeReservation(bytes, out var artifact)
            || artifact is null
            || artifact.SchemaVersion != GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion
            || !string.Equals(GovernedLoopEffectReconciliationArtifactNames.OperationKey(artifact.OperationId), operationKey, StringComparison.Ordinal)
            || !IsHash(artifact.RequestHash)
            || !CustomLoopArtifactIdentifier.IsValid(artifact.ProbeInvocationId, GovernedLoopEffectReconciliationContractLimits.MaxIdentifierCharacters)
            || !CustomLoopArtifactIdentifier.IsValid(artifact.CaseId, GovernedLoopEffectReconciliationContractLimits.MaxIdentifierCharacters)
            || artifact.CaseVersion < 1
            || !IsHash(artifact.CaseContentHash)
            || !IsHash(artifact.BindingHash)
            || !IsHash(artifact.EffectContentHash)
            || string.IsNullOrWhiteSpace(artifact.BindingJson)
            || string.IsNullOrWhiteSpace(artifact.TargetJson)
            || !IsHash(artifact.InputFingerprint)
            || artifact.ReservedAtUtc == default
            || artifact.ReservedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new FormatException("The reconciliation probe reservation is malformed or not bound to its operation identity.");
        }

        var effect = DecodeAttempt(artifact.EffectJson);
        var persistedBinding = Deserialize<GovernedLoopEffectReconciliationBinding>(artifact.BindingJson, "probe binding");
        var source = Deserialize<GovernedLoopEffectReconciliationEvidenceSource>(artifact.SourceJson, "probe source");
        var contract = Deserialize<GovernedLoopEffectReconciliationContractMetadata>(artifact.ContractJson, "probe contract");
        var target = Deserialize<GovernedLoopEffectReconciliationProbeTarget>(artifact.TargetJson, "probe target");
        if (effect is null || persistedBinding is null || source is null || contract is null || target is null
            || GovernedLoopEffectAttemptContract.Validate(effect) is not null
            || !GovernedLoopEffectReconciliationContractValidator.Validate(source).IsValid
            || !GovernedLoopEffectReconciliationContractValidator.Validate(contract).IsValid)
        {
            throw new FormatException("The reconciliation probe reservation contains invalid retained identity evidence.");
        }

        var reference = new GovernedLoopEffectReconciliationCaseReference(artifact.CaseId, artifact.CaseVersion, artifact.CaseContentHash, artifact.BindingHash);
        var chain = await ReadCaseChainAsync(GovernedLoopEffectReconciliationArtifactNames.StorageKey(reference.CaseId), allowMissingHead: false, cancellationToken).ConfigureAwait(false);
        var exactCase = chain.FirstOrDefault(value => value.CaseVersion == reference.CaseVersion && string.Equals(value.ContentHash, reference.ContentHash, StringComparison.Ordinal));
        var binding = exactCase?.Binding;
        if (effect.Payload.Phase != GovernedLoopEffectPhase.ReconciliationRequired
            || binding is null
            || !Equals(persistedBinding, binding)
            || !string.Equals(binding.ContentHash, artifact.BindingHash, StringComparison.Ordinal)
            || !string.Equals(effect.ContentHash, artifact.EffectContentHash, StringComparison.Ordinal)
            || !string.Equals(binding.CurrentAttemptHash, effect.ContentHash, StringComparison.Ordinal)
            || !string.Equals(effect.InputFingerprint, artifact.InputFingerprint, StringComparison.Ordinal)
            || !string.Equals(target.TargetFingerprint, effect.TargetFingerprint, StringComparison.Ordinal)
            || !string.Equals(target.PreconditionEvidenceHash, effect.PreconditionEvidenceHash, StringComparison.Ordinal)
            || !string.Equals(target.BeforeEvidenceId, effect.BeforeEvidenceId, StringComparison.Ordinal)
            || !string.Equals(source.CaseId, reference.CaseId, StringComparison.Ordinal)
            || !string.Equals(source.BindingHash, reference.BindingHash, StringComparison.Ordinal)
            || !string.Equals(source.ReconciliationContractId, contract.ContractId, StringComparison.Ordinal)
            || source.ReconciliationContractVersion != contract.ContractVersion
            || !string.Equals(source.ReconciliationContractHash, contract.ContentHash, StringComparison.Ordinal))
        {
            throw new FormatException("The reconciliation probe reservation contains disconnected identity evidence.");
        }
        if (string.Equals(artifact.ProbeInvocationId, artifact.OperationId, StringComparison.Ordinal)
            || string.Equals(artifact.ProbeInvocationId, binding.OperationId, StringComparison.Ordinal)
            || string.Equals(artifact.ProbeInvocationId, contract.ActuatorOperationId, StringComparison.Ordinal))
        {
            throw new FormatException("The reconciliation probe callback identity is not independent of retained operation identities.");
        }
        var context = new GovernedLoopEffectReconciliationProbeReservationContext(reference, binding, contract, effect, source, target, artifact.InputFingerprint);
        if (!string.Equals(ComputeProbeRequestHash(artifact.OperationId, context), artifact.RequestHash, StringComparison.Ordinal))
        {
            throw new GovernedLoopEffectReconciliationRepairRequiredException("The reconciliation probe reservation request hash does not bind its retained context.");
        }
        var reservation = new GovernedLoopEffectReconciliationProbeReservation(artifact.OperationId, artifact.RequestHash, artifact.ProbeInvocationId, context, artifact.ReservedAtUtc);
        var exactEffect = await _effectAttempts.ReadExactForReconciliationAsync(binding.OperationId, binding.EffectGeneration, effect.ContentHash, cancellationToken).ConfigureAwait(false);
        if (exactCase is null
            || !string.Equals(exactCase.Binding.ContentHash, binding.ContentHash, StringComparison.Ordinal)
            || !string.Equals(exactCase.ContractMetadata.ContentHash, contract.ContentHash, StringComparison.Ordinal)
            || !exactCase.EvidenceSources.Any(value => string.Equals(value.SourceId, source.SourceId, StringComparison.Ordinal) && string.Equals(value.ContentHash, source.ContentHash, StringComparison.Ordinal))
            || exactEffect is null
            || !string.Equals(exactEffect.ContentHash, effect.ContentHash, StringComparison.Ordinal))
        {
            throw new GovernedLoopEffectReconciliationRepairRequiredException("The reconciliation probe reservation is not bound to the exact retained case chain and effect head.");
        }

        return reservation;
    }

    private async Task<GovernedLoopEffectReconciliationProbeObservationArtifact?> ReadProbeObservationAsync(string operationKey, CancellationToken cancellationToken)
    {
        var path = _guard.GetFilePath(_root, GovernedLoopEffectReconciliationArtifactNames.ProbeObservationFileName(operationKey));
        if (!File.Exists(path))
        {
            return null;
        }

        var bytes = await _guard.ReadAllBytesAsync(_root, path, GovernedLoopEffectReconciliationPersistenceLimits.MaximumProbeObservationUtf8Bytes, "Reconciliation probe observation", cancellationToken).ConfigureAwait(false);
        if (!GovernedLoopEffectReconciliationProbeArtifactCodec.TryDecodeObservation(bytes, out var artifact)
            || artifact is null
            || artifact.SchemaVersion != GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion
            || !string.Equals(GovernedLoopEffectReconciliationArtifactNames.OperationKey(artifact.OperationId), operationKey, StringComparison.Ordinal)
            || !IsHash(artifact.RequestHash)
            || !CustomLoopArtifactIdentifier.IsValid(artifact.CaseId, GovernedLoopEffectReconciliationContractLimits.MaxIdentifierCharacters)
            || artifact.CaseVersion < 1
            || !IsHash(artifact.CaseContentHash)
            || !IsHash(artifact.BindingHash)
            || !IsHash(artifact.EffectContentHash)
            || artifact.ResultCaseVersion < 1
            || artifact.ResultCaseContentHash is null
            || !IsHash(artifact.ResultCaseContentHash)
            || string.IsNullOrWhiteSpace(artifact.ObservationJson)
            || !Enum.IsDefined(artifact.Status)
            || artifact.CommittedAtUtc == default
            || artifact.CommittedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new FormatException("The reconciliation probe observation is malformed or not bound to its operation identity.");
        }

        var payload = Deserialize<GovernedLoopEffectReconciliationObservation>(artifact.ObservationJson!, "probe observation");
        if (payload is null
            || !GovernedLoopEffectReconciliationContractValidator.Validate(payload).IsValid
            || !string.Equals(payload.CaseId, artifact.CaseId, StringComparison.Ordinal)
            || !string.Equals(payload.BindingHash, artifact.BindingHash, StringComparison.Ordinal)
            || !IsObservationStatusCompatible(artifact.Status, payload.Kind)
            || !string.Equals(payload.ObservationId, ProbeObservationId(artifact.OperationId), StringComparison.Ordinal)
            || payload.RecordedAtUtc > artifact.CommittedAtUtc
            || payload.ObservedAtUtc is { } observedAt && observedAt > artifact.CommittedAtUtc)
        {
            throw new FormatException("The reconciliation probe observation payload is not canonically bound to its receipt.");
        }

        return artifact;
    }

    private async Task<GovernedLoopEffectReconciliationProbeObservationCommitResult> ReadProbeCommitPayloadAsync(
        GovernedLoopEffectReconciliationProbeObservationArtifact observation,
        GovernedLoopEffectReconciliationProbeReservation reservation,
        CancellationToken cancellationToken)
    {
        var key = GovernedLoopEffectReconciliationArtifactNames.StorageKey(reservation.Context.Case.CaseId);
        var chain = await ReadCaseChainAsync(key, allowMissingHead: false, cancellationToken).ConfigureAwait(false);
        var exact = chain.FirstOrDefault(value => value.CaseVersion == observation.ResultCaseVersion && string.Equals(value.ContentHash, observation.ResultCaseContentHash, StringComparison.Ordinal));
        var effect = await _effectAttempts.ReadExactForReconciliationAsync(reservation.Context.EffectHead.Payload.OperationId, reservation.Context.EffectHead.Payload.EffectGeneration, reservation.Context.EffectHead.ContentHash, cancellationToken).ConfigureAwait(false);
        var payload = Deserialize<GovernedLoopEffectReconciliationObservation>(observation.ObservationJson!, "probe observation");
        var predecessor = chain.FirstOrDefault(value => value.CaseVersion == reservation.Context.Case.CaseVersion && string.Equals(value.ContentHash, reservation.Context.Case.ContentHash, StringComparison.Ordinal));
        var expected = payload is not null && predecessor is not null
            ? CreateProbeResultCase(predecessor, payload, observation.CommittedAtUtc)
            : null;
        if (payload is null
            || !string.Equals(observation.OperationId, reservation.OperationId, StringComparison.Ordinal)
            || !string.Equals(observation.RequestHash, reservation.RequestHash, StringComparison.Ordinal)
            || !string.Equals(observation.CaseId, reservation.Context.Case.CaseId, StringComparison.Ordinal)
            || observation.CaseVersion != reservation.Context.Case.CaseVersion
            || !string.Equals(observation.CaseContentHash, reservation.Context.Case.ContentHash, StringComparison.Ordinal)
            || !string.Equals(observation.BindingHash, reservation.Context.Binding.ContentHash, StringComparison.Ordinal)
            || !string.Equals(observation.EffectContentHash, reservation.Context.EffectHead.ContentHash, StringComparison.Ordinal)
            || observation.ResultCaseVersion != reservation.Context.Case.CaseVersion + 1
            || observation.CommittedAtUtc < reservation.ReservedAtUtc
            || exact is null
            || predecessor is null
            || expected is null
            || !string.Equals(observation.ResultCaseContentHash, expected.ContentHash, StringComparison.Ordinal)
            || !string.Equals(exact.ContentHash, expected.ContentHash, StringComparison.Ordinal)
            || !string.Equals(payload.CaseId, reservation.Context.Case.CaseId, StringComparison.Ordinal)
            || !string.Equals(payload.BindingHash, reservation.Context.Binding.ContentHash, StringComparison.Ordinal)
            || !string.Equals(payload.SourceId, reservation.Context.Source.SourceId, StringComparison.Ordinal)
            || !string.Equals(payload.SourceRegistrationHash, reservation.Context.Source.ContentHash, StringComparison.Ordinal)
            || effect is null || !string.Equals(effect.ContentHash, reservation.Context.EffectHead.ContentHash, StringComparison.Ordinal))
        {
            throw new GovernedLoopEffectReconciliationRepairRequiredException("The probe observation receipt does not point to a complete immutable case result.");
        }

        return new GovernedLoopEffectReconciliationProbeObservationCommitResult(GovernedLoopEffectReconciliationProbeReservationStatus.Replayed, exact, effect);
    }

    private static GovernedLoopEffectReconciliationCase CreateProbeResultCase(
        GovernedLoopEffectReconciliationCase predecessor,
        GovernedLoopEffectReconciliationObservation observation,
        DateTimeOffset committedAtUtc)
        => GovernedLoopEffectReconciliationContract.Create(
            predecessor.CaseId,
            predecessor.CaseVersion + 1,
            predecessor.Binding,
            predecessor.ContractMetadata,
            predecessor.EvidenceSources,
            [.. predecessor.ObservationHistory, observation],
            predecessor.AssessmentHistory,
            predecessor.CurrentAssessmentHash,
            predecessor.Disposition,
            predecessor.Resolution,
            predecessor.CaseReceiptHashes,
            predecessor.ContentHash,
            predecessor.OpenedAtUtc,
            committedAtUtc);

    private GovernedLoopEffectReconciliationObservation NormalizeProbeObservation(
        GovernedLoopEffectReconciliationProbeInvocationResult result,
        GovernedLoopEffectReconciliationProbeReservation reservation,
        DateTimeOffset now)
    {
        if (result.Status == GovernedLoopEffectReconciliationProbeInvocationStatus.Ready && result.Observation is not null)
        {
            var value = result.Observation;
            if (!string.Equals(value.CaseId, reservation.Context.Case.CaseId, StringComparison.Ordinal)
                || !string.Equals(value.BindingHash, reservation.Context.Case.BindingHash, StringComparison.Ordinal)
                || !string.Equals(value.SourceId, reservation.Context.Source.SourceId, StringComparison.Ordinal)
                || !string.Equals(value.SourceRegistrationHash, reservation.Context.Source.ContentHash, StringComparison.Ordinal)
                || value.RecordedAtUtc > now
                || value.ObservedAtUtc is { } observedAt && observedAt > now
                || !GovernedLoopEffectReconciliationContractValidator.Validate(value).IsValid)
            {
                throw new FormatException("The probe returned an observation outside its reserved case, binding, or source identity.");
            }

            return GovernedLoopEffectReconciliationContractHash.Apply(value with
            {
                ObservationId = ProbeObservationId(reservation.OperationId),
                ContentHash = string.Empty
            });
        }

        var kind = result.Status switch
        {
            GovernedLoopEffectReconciliationProbeInvocationStatus.NotFound => GovernedLoopEffectReconciliationObservationKind.Missing,
            GovernedLoopEffectReconciliationProbeInvocationStatus.Invalid => GovernedLoopEffectReconciliationObservationKind.UnprovenHash,
            _ => GovernedLoopEffectReconciliationObservationKind.Missing
        };
        var observation = new GovernedLoopEffectReconciliationObservation(
            GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
            reservation.Context.Case.CaseId,
            reservation.Context.Case.BindingHash,
            ProbeObservationId(reservation.OperationId),
            reservation.Context.Source.SourceId,
            reservation.Context.Source.ContentHash,
            kind,
            reservation.Context.Source.ReliabilityPosture,
            GovernedLoopEffectReconciliationObservedOutcome.Unknown,
            null,
            null,
            null,
            now,
            "The read-only probe did not establish exact external evidence.",
            string.Empty);
        return GovernedLoopEffectReconciliationContractHash.Apply(observation);
    }

    private static string ProbeObservationId(string operationId)
        => "probe-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operationId))).ToLowerInvariant()[..32];

    private static string CreateProbeInvocationId()
        => "probe-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static bool IsObservationStatusCompatible(
        GovernedLoopEffectReconciliationProbeInvocationStatus status,
        GovernedLoopEffectReconciliationObservationKind kind)
        => status switch
        {
            GovernedLoopEffectReconciliationProbeInvocationStatus.Ready => kind is GovernedLoopEffectReconciliationObservationKind.Evidence
                or GovernedLoopEffectReconciliationObservationKind.Missing
                or GovernedLoopEffectReconciliationObservationKind.TimedOut
                or GovernedLoopEffectReconciliationObservationKind.Cancelled
                or GovernedLoopEffectReconciliationObservationKind.UnprovenHash,
            GovernedLoopEffectReconciliationProbeInvocationStatus.NotFound => kind == GovernedLoopEffectReconciliationObservationKind.Missing,
            GovernedLoopEffectReconciliationProbeInvocationStatus.Invalid => kind == GovernedLoopEffectReconciliationObservationKind.UnprovenHash,
            GovernedLoopEffectReconciliationProbeInvocationStatus.Unavailable => kind == GovernedLoopEffectReconciliationObservationKind.Missing,
            _ => false,
        };

    private static string ComputeProbeRequestHash(string operationId, GovernedLoopEffectReconciliationProbeReservationContext context)
    {
        var builder = new StringBuilder(2048);
        Append(builder, "embodysense.governed-loop-effect-reconciliation-probe.v1");
        Append(builder, operationId);
        Append(builder, context.Case.CaseId);
        Append(builder, context.Case.CaseVersion);
        Append(builder, context.Case.ContentHash);
        Append(builder, context.Binding.ContentHash);
        Append(builder, context.EffectHead.ContentHash);
        Append(builder, context.InputFingerprint);
        Append(builder, context.Target.TargetFingerprint);
        Append(builder, context.Target.PreconditionEvidenceHash);
        Append(builder, context.Target.BeforeEvidenceId);
        Append(builder, context.Source.SourceId);
        Append(builder, context.Source.ContentHash);
        Append(builder, context.Source.RegistrationEvidenceHash);
        Append(builder, context.Contract.ContentHash);
        Append(builder, context.Contract.ProbeContractId);
        Append(builder, context.Contract.ProbeContractVersion);
        Append(builder, context.Contract.ProbeContractHash);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static void Append(StringBuilder builder, string? value)
    {
        if (value is null)
        {
            builder.Append("-1:");
            return;
        }

        builder.Append(Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(value);
    }

    private static void Append(StringBuilder builder, long value) => Append(builder, value.ToString(CultureInfo.InvariantCulture));

    private static T? Deserialize<T>(string json, string description)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, _probeJson);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException)
        {
            throw new FormatException($"The {description} payload is malformed.", exception);
        }
    }

    private async Task WriteProbeReservationAsync(string operationKey, GovernedLoopEffectReconciliationProbeReservationArtifact artifact, CancellationToken cancellationToken)
    {
        var path = _guard.GetFilePath(_root, GovernedLoopEffectReconciliationArtifactNames.ProbeReservationFileName(operationKey));
        var bytes = GovernedLoopEffectReconciliationProbeArtifactCodec.EncodeReservation(artifact);
        if (!await _guard.WriteTextAtomicallyIfAbsentAsync(_root, path, Encoding.UTF8.GetString(bytes), cancellationToken).ConfigureAwait(false))
        {
            var existing = await _guard.ReadAllBytesAsync(_root, path, GovernedLoopEffectReconciliationPersistenceLimits.MaximumProbeReservationUtf8Bytes, "Reconciliation probe reservation", cancellationToken).ConfigureAwait(false);
            if (!existing.SequenceEqual(bytes))
            {
                throw new GovernedLoopEffectReconciliationRepairRequiredException("A probe reservation conflicted with immutable evidence.");
            }
        }
    }

    private async Task WriteProbeObservationAsync(string operationKey, GovernedLoopEffectReconciliationProbeObservationArtifact artifact, CancellationToken cancellationToken)
    {
        var path = _guard.GetFilePath(_root, GovernedLoopEffectReconciliationArtifactNames.ProbeObservationFileName(operationKey));
        var bytes = GovernedLoopEffectReconciliationProbeArtifactCodec.EncodeObservation(artifact);
        if (!await _guard.WriteTextAtomicallyIfAbsentAsync(_root, path, Encoding.UTF8.GetString(bytes), cancellationToken).ConfigureAwait(false))
        {
            var existing = await _guard.ReadAllBytesAsync(_root, path, GovernedLoopEffectReconciliationPersistenceLimits.MaximumProbeObservationUtf8Bytes, "Reconciliation probe observation", cancellationToken).ConfigureAwait(false);
            if (!existing.SequenceEqual(bytes))
            {
                throw new GovernedLoopEffectReconciliationRepairRequiredException("A probe observation conflicted with immutable evidence.");
            }
        }
    }

    private async Task WriteProbeJournalIfAbsentAsync(string operationKey, GovernedLoopEffectReconciliationProbeJournal journal, CancellationToken cancellationToken)
    {
        var path = _guard.GetFilePath(_root, GovernedLoopEffectReconciliationArtifactNames.ProbeJournalFileName(operationKey));
        var bytes = GovernedLoopEffectReconciliationProbeArtifactCodec.EncodeJournal(journal);
        if (!await _guard.WriteTextAtomicallyIfAbsentAsync(_root, path, Encoding.UTF8.GetString(bytes), cancellationToken).ConfigureAwait(false))
        {
            var existing = await _guard.ReadAllBytesAsync(_root, path, GovernedLoopEffectReconciliationPersistenceLimits.MaximumProbeJournalUtf8Bytes, "Reconciliation probe journal", cancellationToken).ConfigureAwait(false);
            if (!existing.SequenceEqual(bytes))
            {
                throw new GovernedLoopEffectReconciliationRepairRequiredException("A probe journal conflicted with immutable evidence.");
            }
        }
    }

    private async Task WriteProbeJournalAsync(string operationKey, GovernedLoopEffectReconciliationProbeJournal journal, CancellationToken cancellationToken)
    {
        var path = _guard.GetFilePath(_root, GovernedLoopEffectReconciliationArtifactNames.ProbeJournalFileName(operationKey));
        var bytes = GovernedLoopEffectReconciliationProbeArtifactCodec.EncodeJournal(journal);
        await _guard.WriteTextAtomicallyAsync(_root, path, Encoding.UTF8.GetString(bytes), cancellationToken).ConfigureAwait(false);
    }

    private async Task<GovernedLoopEffectReconciliationProbeJournal?> ReadProbeJournalAsync(string operationKey, CancellationToken cancellationToken)
    {
        var path = _guard.GetFilePath(_root, GovernedLoopEffectReconciliationArtifactNames.ProbeJournalFileName(operationKey));
        if (!File.Exists(path))
        {
            return null;
        }

        var bytes = await _guard.ReadAllBytesAsync(_root, path, GovernedLoopEffectReconciliationPersistenceLimits.MaximumProbeJournalUtf8Bytes, "Reconciliation probe journal", cancellationToken).ConfigureAwait(false);
        if (!GovernedLoopEffectReconciliationProbeArtifactCodec.TryDecodeJournal(bytes, out var journal)
            || journal is null
            || journal.SchemaVersion != GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion
            || !string.Equals(GovernedLoopEffectReconciliationArtifactNames.OperationKey(journal.OperationId), operationKey, StringComparison.Ordinal)
            || !IsHash(journal.RequestHash)
            || !Enum.IsDefined(journal.Stage)
            || journal.CreatedAtUtc == default
            || journal.CreatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new FormatException("The reconciliation probe journal is malformed or not bound to its operation identity.");
        }

        if (!GovernedLoopEffectReconciliationProbeArtifactCodec.TryDecodeReservation(Encoding.UTF8.GetBytes(journal.ReservationJson), out var reservation)
            || reservation is null
            || !GovernedLoopEffectReconciliationProbeArtifactCodec.TryDecodeObservation(Encoding.UTF8.GetBytes(journal.ObservationJson), out var observation)
            || observation is null
            || !string.Equals(reservation.OperationId, journal.OperationId, StringComparison.Ordinal)
            || !string.Equals(observation.OperationId, journal.OperationId, StringComparison.Ordinal)
            || !string.Equals(reservation.RequestHash, journal.RequestHash, StringComparison.Ordinal)
            || !string.Equals(observation.RequestHash, journal.RequestHash, StringComparison.Ordinal))
        {
            throw new GovernedLoopEffectReconciliationRepairRequiredException("The reconciliation probe journal does not contain matching canonical reservation and observation artifacts.");
        }

        return journal;
    }

    private void DeleteProbeJournal(string operationKey)
        => _guard.DeleteFile(_root, _guard.GetFilePath(_root, GovernedLoopEffectReconciliationArtifactNames.ProbeJournalFileName(operationKey)));

    private async Task RecoverPendingProbeJournalsUnderLockAsync(CancellationToken cancellationToken)
    {
        var paths = Directory.EnumerateFiles(_root, GovernedLoopEffectReconciliationPersistenceLimits.ProbeJournalFilePrefix + "*.json", SearchOption.TopDirectoryOnly)
            .Where(path => GovernedLoopEffectReconciliationArtifactNames.TryParseProbeJournalFile(Path.GetFileName(path), out _))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (paths.Length > GovernedLoopEffectReconciliationPersistenceLimits.MaximumProbeJournals)
        {
            throw new GovernedLoopEffectReconciliationRepairRequiredException("Too many interrupted reconciliation probe publications require explicit repair.");
        }

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!GovernedLoopEffectReconciliationArtifactNames.TryParseProbeJournalFile(Path.GetFileName(path), out var operationKey))
            {
                throw new FormatException("The reconciliation probe journal name is malformed.");
            }
            var journal = await ReadProbeJournalAsync(operationKey, cancellationToken).ConfigureAwait(false)
                ?? throw new FormatException("The reconciliation probe journal disappeared during recovery.");
            var reservation = await ReadProbeReservationAsync(operationKey, cancellationToken).ConfigureAwait(false);
            var observation = Deserialize<GovernedLoopEffectReconciliationProbeObservationArtifact>(journal.ObservationJson, "probe observation journal");
            var embeddedReservation = Deserialize<GovernedLoopEffectReconciliationProbeReservationArtifact>(journal.ReservationJson, "probe reservation journal");
            if (reservation is null || observation is null || embeddedReservation is null
                || !string.Equals(reservation.RequestHash, journal.RequestHash, StringComparison.Ordinal)
                || !string.Equals(Encoding.UTF8.GetString(GovernedLoopEffectReconciliationProbeArtifactCodec.EncodeReservation(embeddedReservation)), journal.ReservationJson, StringComparison.Ordinal)
                || !string.Equals(Encoding.UTF8.GetString(GovernedLoopEffectReconciliationProbeArtifactCodec.EncodeReservation(CreateReservationArtifact(reservation))), journal.ReservationJson, StringComparison.Ordinal)
                || !string.Equals(observation.OperationId, reservation.OperationId, StringComparison.Ordinal)
                || !string.Equals(observation.RequestHash, reservation.RequestHash, StringComparison.Ordinal)
                || !string.Equals(observation.CaseId, reservation.Context.Case.CaseId, StringComparison.Ordinal)
                || observation.CaseVersion != reservation.Context.Case.CaseVersion
                || !string.Equals(observation.CaseContentHash, reservation.Context.Case.ContentHash, StringComparison.Ordinal)
                || !string.Equals(observation.BindingHash, reservation.Context.Binding.ContentHash, StringComparison.Ordinal)
                || !string.Equals(observation.EffectContentHash, reservation.Context.EffectHead.ContentHash, StringComparison.Ordinal)
                || observation.ResultCaseVersion != reservation.Context.Case.CaseVersion + 1
                || observation.CommittedAtUtc == default
                || observation.CommittedAtUtc.Offset != TimeSpan.Zero
                || observation.CommittedAtUtc < reservation.ReservedAtUtc)
            {
                throw new GovernedLoopEffectReconciliationRepairRequiredException("The reconciliation probe journal is not bound to its immutable reservation.");
            }
            var storedObservation = Deserialize<GovernedLoopEffectReconciliationObservation>(observation.ObservationJson ?? string.Empty, "probe observation");
            if (storedObservation is null
                || !string.Equals(storedObservation.CaseId, reservation.Context.Case.CaseId, StringComparison.Ordinal)
                || !string.Equals(storedObservation.BindingHash, reservation.Context.Case.BindingHash, StringComparison.Ordinal)
                || !string.Equals(storedObservation.SourceId, reservation.Context.Source.SourceId, StringComparison.Ordinal)
                || !string.Equals(storedObservation.SourceRegistrationHash, reservation.Context.Source.ContentHash, StringComparison.Ordinal)
                || !string.Equals(storedObservation.ObservationId, ProbeObservationId(reservation.OperationId), StringComparison.Ordinal)
                || storedObservation.RecordedAtUtc > observation.CommittedAtUtc
                || storedObservation.ObservedAtUtc is { } observedAt && observedAt > observation.CommittedAtUtc
                || !GovernedLoopEffectReconciliationContractValidator.Validate(storedObservation).IsValid
                || !IsObservationStatusCompatible(observation.Status, storedObservation.Kind))
            {
                throw new GovernedLoopEffectReconciliationRepairRequiredException("The reconciliation probe journal has an invalid immutable observation payload.");
            }
            var key = GovernedLoopEffectReconciliationArtifactNames.StorageKey(reservation.Context.Case.CaseId);
            var chain = await ReadCaseChainAsync(key, allowMissingHead: false, cancellationToken).ConfigureAwait(false);
            var current = chain.LastOrDefault();
            var predecessor = chain.FirstOrDefault(value => value.CaseVersion == reservation.Context.Case.CaseVersion && string.Equals(value.ContentHash, reservation.Context.Case.ContentHash, StringComparison.Ordinal));
            if (predecessor is null)
            {
                throw new GovernedLoopEffectReconciliationRepairRequiredException("The interrupted reconciliation probe is missing its immutable predecessor.");
            }
            var expected = CreateProbeResultCase(predecessor, storedObservation, observation.CommittedAtUtc);
            if (expected.CaseVersion != observation.ResultCaseVersion || !string.Equals(expected.ContentHash, observation.ResultCaseContentHash, StringComparison.Ordinal))
            {
                throw new GovernedLoopEffectReconciliationRepairRequiredException("The interrupted reconciliation probe result case does not match its immutable receipt.");
            }
            if (current is not null && string.Equals(current.ContentHash, reservation.Context.Case.ContentHash, StringComparison.Ordinal))
            {
                await WriteProbeObservationAsync(operationKey, observation, cancellationToken).ConfigureAwait(false);
                await PublishCaseAsync(expected, key, cancellationToken).ConfigureAwait(false);
            }
            else if (current is null
                || !string.Equals(current.ContentHash, expected.ContentHash, StringComparison.Ordinal))
            {
                throw new GovernedLoopEffectReconciliationRepairRequiredException("The interrupted reconciliation probe has a conflicting case head.");
            }
            DeleteProbeJournal(operationKey);
        }
    }

    private async Task ValidateProbeInventoryAsync(IReadOnlyList<GovernedLoopEffectReconciliationCase> cases, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        var reservations = Directory.EnumerateFiles(_root, GovernedLoopEffectReconciliationPersistenceLimits.ProbeReservationFilePrefix + "*.json", SearchOption.TopDirectoryOnly)
            .Count(path => GovernedLoopEffectReconciliationArtifactNames.TryParseProbeReservationFile(Path.GetFileName(path), out _));
        var observations = Directory.EnumerateFiles(_root, GovernedLoopEffectReconciliationPersistenceLimits.ProbeObservationFilePrefix + "*.json", SearchOption.TopDirectoryOnly)
            .Count(path => GovernedLoopEffectReconciliationArtifactNames.TryParseProbeObservationFile(Path.GetFileName(path), out _));
        var journals = Directory.EnumerateFiles(_root, GovernedLoopEffectReconciliationPersistenceLimits.ProbeJournalFilePrefix + "*.json", SearchOption.TopDirectoryOnly)
            .Count(path => GovernedLoopEffectReconciliationArtifactNames.TryParseProbeJournalFile(Path.GetFileName(path), out _));
        if (reservations > GovernedLoopEffectReconciliationPersistenceLimits.MaximumProbeReservations
            || observations > GovernedLoopEffectReconciliationPersistenceLimits.MaximumProbeObservations
            || journals > GovernedLoopEffectReconciliationPersistenceLimits.MaximumProbeJournals)
        {
            throw new FormatException("The reconciliation probe artifact inventory exceeds its finite bounds.");
        }

        var reservationsByCase = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(_root, GovernedLoopEffectReconciliationPersistenceLimits.ProbeReservationFilePrefix + "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!GovernedLoopEffectReconciliationArtifactNames.TryParseProbeReservationFile(Path.GetFileName(path), out var operationKey))
            {
                throw new FormatException("The reconciliation probe reservation name is malformed.");
            }
            var reservation = await ReadProbeReservationAsync(operationKey, cancellationToken).ConfigureAwait(false)
                ?? throw new FormatException("The reconciliation probe reservation disappeared during inventory validation.");
            if (!cases.Any(value => string.Equals(value.CaseId, reservation.Context.Case.CaseId, StringComparison.Ordinal)))
            {
                throw new FormatException("The reconciliation probe reservation is not attached to a retained case.");
            }
            var caseReservations = reservationsByCase.GetValueOrDefault(reservation.Context.Case.CaseId) + 1;
            if (caseReservations > GovernedLoopEffectReconciliationPersistenceLimits.MaximumProbeReservationsPerCase)
            {
                throw new FormatException("The reconciliation probe reservation inventory exceeds its per-case bound.");
            }
            reservationsByCase[reservation.Context.Case.CaseId] = caseReservations;
        }

        foreach (var path in Directory.EnumerateFiles(_root, GovernedLoopEffectReconciliationPersistenceLimits.ProbeObservationFilePrefix + "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!GovernedLoopEffectReconciliationArtifactNames.TryParseProbeObservationFile(Path.GetFileName(path), out var operationKey))
            {
                throw new FormatException("The reconciliation probe observation name is malformed.");
            }
            var observation = await ReadProbeObservationAsync(operationKey, cancellationToken).ConfigureAwait(false)
                ?? throw new FormatException("The reconciliation probe observation disappeared during inventory validation.");
            var reservation = await ReadProbeReservationAsync(operationKey, cancellationToken).ConfigureAwait(false)
                ?? throw new FormatException("The reconciliation probe observation is orphaned from its reservation.");
            if (!string.Equals(observation.RequestHash, reservation.RequestHash, StringComparison.Ordinal)
                || !string.Equals(observation.EffectContentHash, reservation.Context.EffectHead.ContentHash, StringComparison.Ordinal))
            {
                throw new FormatException("The reconciliation probe observation is not bound to its reservation.");
            }
            _ = await ReadProbeCommitPayloadAsync(observation, reservation, cancellationToken).ConfigureAwait(false);
        }
    }

    private int CountProbeReservationFiles()
        => Directory.Exists(_root)
            ? Directory.EnumerateFiles(_root, GovernedLoopEffectReconciliationPersistenceLimits.ProbeReservationFilePrefix + "*.json", SearchOption.TopDirectoryOnly)
                .Count(path => GovernedLoopEffectReconciliationArtifactNames.TryParseProbeReservationFile(Path.GetFileName(path), out _))
            : 0;

    private int CountProbeObservationFiles()
        => Directory.Exists(_root)
            ? Directory.EnumerateFiles(_root, GovernedLoopEffectReconciliationPersistenceLimits.ProbeObservationFilePrefix + "*.json", SearchOption.TopDirectoryOnly)
                .Count(path => GovernedLoopEffectReconciliationArtifactNames.TryParseProbeObservationFile(Path.GetFileName(path), out _))
            : 0;

    private int CountIncompleteProbeReservationFiles()
    {
        if (!Directory.Exists(_root))
        {
            return 0;
        }

        var count = 0;
        foreach (var path in Directory.EnumerateFiles(_root, GovernedLoopEffectReconciliationPersistenceLimits.ProbeReservationFilePrefix + "*.json", SearchOption.TopDirectoryOnly))
        {
            if (GovernedLoopEffectReconciliationArtifactNames.TryParseProbeReservationFile(Path.GetFileName(path), out var operationKey)
                && !File.Exists(_guard.GetFilePath(_root, GovernedLoopEffectReconciliationArtifactNames.ProbeObservationFileName(operationKey))))
            {
                count++;
            }
        }

        return count;
    }

    private async Task<bool> HasIncompleteProbeReservationForCaseAsync(
        GovernedLoopEffectReconciliationCaseReference caseReference,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_root))
        {
            return false;
        }

        foreach (var path in Directory.EnumerateFiles(_root, GovernedLoopEffectReconciliationPersistenceLimits.ProbeReservationFilePrefix + "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!GovernedLoopEffectReconciliationArtifactNames.TryParseProbeReservationFile(Path.GetFileName(path), out var operationKey)
                || File.Exists(_guard.GetFilePath(_root, GovernedLoopEffectReconciliationArtifactNames.ProbeObservationFileName(operationKey))))
            {
                continue;
            }

            var reservation = await ReadProbeReservationAsync(operationKey, cancellationToken).ConfigureAwait(false);
            if (reservation is not null
                && string.Equals(reservation.Context.Case.CaseId, caseReference.CaseId, StringComparison.Ordinal)
                && reservation.Context.Case.CaseVersion == caseReference.CaseVersion
                && string.Equals(reservation.Context.Case.ContentHash, caseReference.ContentHash, StringComparison.Ordinal)
                && string.Equals(reservation.Context.Case.BindingHash, caseReference.BindingHash, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<int> CountProbeReservationsForCaseAsync(string caseId, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_root))
        {
            return 0;
        }

        var count = 0;
        foreach (var path in Directory.EnumerateFiles(_root, GovernedLoopEffectReconciliationPersistenceLimits.ProbeReservationFilePrefix + "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GovernedLoopEffectReconciliationArtifactNames.TryParseProbeReservationFile(Path.GetFileName(path), out var key)
                && await ReadProbeReservationAsync(key, cancellationToken).ConfigureAwait(false) is { Context.Case.CaseId: var id }
                && string.Equals(id, caseId, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    private bool HasProbeRetainedCapacity(
        GovernedLoopEffectReconciliationProbeReservationArtifact? reservation,
        GovernedLoopEffectReconciliationProbeObservationArtifact? observation,
        GovernedLoopEffectReconciliationProbeJournal? journal,
        GovernedLoopEffectReconciliationCase? nextCase,
        bool includeReservationBudget,
        bool excludeCurrentReservationBudget = false)
    {
        try
        {
            var publicationBytes = checked((long)GovernedLoopEffectReconciliationPersistenceLimits.MaximumProbeObservationUtf8Bytes
                + GovernedLoopEffectReconciliationPersistenceLimits.MaximumProbeJournalUtf8Bytes
                + GovernedLoopEffectReconciliationContractLimits.MaxRecordUtf8Bytes);
            var heldReservationCount = CountIncompleteProbeReservationFiles() - (excludeCurrentReservationBudget ? 1 : 0);
            var heldPublicationBytes = checked((long)Math.Max(0, heldReservationCount) * publicationBytes);
            var additionalBytes = checked((reservation is null ? 0 : GovernedLoopEffectReconciliationProbeArtifactCodec.EncodeReservation(reservation).Length)
                + (observation is null ? 0 : GovernedLoopEffectReconciliationProbeArtifactCodec.EncodeObservation(observation).Length)
                + (journal is null ? 0 : Math.Max(
                    GovernedLoopEffectReconciliationProbeArtifactCodec.EncodeJournal(journal).Length,
                    GovernedLoopEffectReconciliationProbeArtifactCodec.EncodeJournal(journal with { Stage = GovernedLoopEffectReconciliationProbeJournalStage.ObservationPublished, ContentHash = string.Empty }).Length))
                + (nextCase is null ? 0 : GovernedLoopEffectReconciliationRecordCodec.Encode(nextCase).Length)
                + heldPublicationBytes
                + (includeReservationBudget ? publicationBytes : 0));
            return additionalBytes <= _effectAttempts.MaximumStoreBytes
                && _effectAttempts.GetRetainedBytesUnderMutationLock() <= _effectAttempts.MaximumStoreBytes - additionalBytes;
        }
        catch (OverflowException)
        {
            return false;
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
            await ValidateProbeInventoryAsync(allCases, cancellationToken).ConfigureAwait(false);
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
        await ValidateProbeInventoryAsync(allCases, cancellationToken).ConfigureAwait(false);
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
        if (currentCase is not null
            && await HasIncompleteProbeReservationForCaseAsync(
                new GovernedLoopEffectReconciliationCaseReference(currentCase.CaseId, currentCase.CaseVersion, currentCase.ContentHash, currentCase.Binding.ContentHash),
                cancellationToken).ConfigureAwait(false))
        {
            return CurrentResult(GovernedLoopEffectReconciliationCaseMutationStatus.Conflict, currentCase, currentEffect);
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

        await RecoverPendingProbeJournalsUnderLockAsync(cancellationToken).ConfigureAwait(false);
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
                .Any(path => GovernedLoopEffectReconciliationArtifactNames.TryParseJournalFile(Path.GetFileName(path), out _))
            || Directory.Exists(_root)
                && Directory.EnumerateFiles(_root, GovernedLoopEffectReconciliationPersistenceLimits.ProbeJournalFilePrefix + "*.json", SearchOption.TopDirectoryOnly)
                    .Any(path => GovernedLoopEffectReconciliationArtifactNames.TryParseProbeJournalFile(Path.GetFileName(path), out _));

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

        foreach (var path in Directory.EnumerateFiles(_root, GovernedLoopEffectReconciliationPersistenceLimits.ProbeJournalFilePrefix + "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GovernedLoopEffectReconciliationArtifactNames.TryParseProbeJournalFile(Path.GetFileName(path), out var operationKey))
            {
                _ = await ReadProbeJournalAsync(operationKey, cancellationToken).ConfigureAwait(false)
                    ?? throw new FormatException("The reconciliation probe journal disappeared during validation.");
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

    private static GovernedLoopEffectReconciliationProbeReservationResult ProbeReservationResult(GovernedLoopEffectReconciliationProbeReservationStatus status)
        => new(status, null);

    private static GovernedLoopEffectReconciliationProbeObservationCommitResult ProbeCommitResult(GovernedLoopEffectReconciliationProbeReservationStatus status)
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

    private static bool SameProbeReservation(
        GovernedLoopEffectReconciliationProbeReservation left,
        GovernedLoopEffectReconciliationProbeReservation right)
        => string.Equals(left.OperationId, right.OperationId, StringComparison.Ordinal)
            && string.Equals(left.RequestHash, right.RequestHash, StringComparison.Ordinal)
            && string.Equals(left.ProbeInvocationId, right.ProbeInvocationId, StringComparison.Ordinal)
            && left.ReservedAtUtc == right.ReservedAtUtc
            && Equals(left.Context.Case, right.Context.Case)
            && string.Equals(left.Context.Binding.ContentHash, right.Context.Binding.ContentHash, StringComparison.Ordinal)
            && string.Equals(left.Context.EffectHead.ContentHash, right.Context.EffectHead.ContentHash, StringComparison.Ordinal)
            && string.Equals(left.Context.InputFingerprint, right.Context.InputFingerprint, StringComparison.Ordinal)
            && string.Equals(left.Context.Target.TargetFingerprint, right.Context.Target.TargetFingerprint, StringComparison.Ordinal)
            && string.Equals(left.Context.Target.PreconditionEvidenceHash, right.Context.Target.PreconditionEvidenceHash, StringComparison.Ordinal)
            && string.Equals(left.Context.Target.BeforeEvidenceId, right.Context.Target.BeforeEvidenceId, StringComparison.Ordinal)
            && string.Equals(left.Context.Source.SourceId, right.Context.Source.SourceId, StringComparison.Ordinal)
            && string.Equals(left.Context.Source.ContentHash, right.Context.Source.ContentHash, StringComparison.Ordinal)
            && string.Equals(left.Context.Contract.ContractId, right.Context.Contract.ContractId, StringComparison.Ordinal)
            && left.Context.Contract.ContractVersion == right.Context.Contract.ContractVersion
            && string.Equals(left.Context.Contract.ContentHash, right.Context.Contract.ContentHash, StringComparison.Ordinal);

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
