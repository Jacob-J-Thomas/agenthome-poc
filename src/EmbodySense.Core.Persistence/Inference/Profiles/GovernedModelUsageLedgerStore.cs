using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Inference.Profiles;
using EmbodySense.Core.Application.Inference.Profiles.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.Inference.Profiles;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Inference.Profiles.Models;

namespace EmbodySense.Core.Persistence.Inference.Profiles;

/// <summary>Persists one crash-safe, authenticated, segmented append-only provider-usage ledger per physical workspace.</summary>
/// <remarks>
/// Reservation vectors are derived atomically from exact per-attempt, node-series, and run-wide remaining budgets;
/// callers cannot select the durable amount. Unknown usage retains its reservation, affirmative release is honored,
/// and every cross-process mutation shares the capability-authority transaction and a retained-handle file lock. Full
/// segments rotate to immutable authenticated archives, preserving exact historical evidence without exhausting future runs.
/// </remarks>
public sealed class GovernedModelUsageLedgerStore : IGovernedModelUsageLedger
{
    private readonly GovernedModelUsageLedgerStoreOptions _options;
    private readonly GovernedModelUsageLedgerStorePaths _paths;
    private readonly AuthenticatedModelPersistenceStore<GovernedModelUsageLedgerStoreDocument> _store;
    private readonly ICapabilityAuthorityTransaction _authorityTransaction;
    private readonly string _workspaceId;

    /// <summary>Creates a ledger with the default server-owned trust provider.</summary>
    public GovernedModelUsageLedgerStore(WorkspacePaths paths, GovernedModelUsageLedgerStoreOptions? options = null, ICapabilityCatalogDurabilityBarrier? durabilityBarrier = null, ICapabilityAuthorityTransaction? authorityTransaction = null)
        : this(paths, FileCapabilityCatalogTrustProvider.CreateDefault(), options, durabilityBarrier, authorityTransaction)
    {
    }

    /// <summary>Creates a ledger with an explicit server-owned trust provider.</summary>
    public GovernedModelUsageLedgerStore(WorkspacePaths paths, ICapabilityCatalogTrustProvider trustProvider, GovernedModelUsageLedgerStoreOptions? options = null, ICapabilityCatalogDurabilityBarrier? durabilityBarrier = null, ICapabilityAuthorityTransaction? authorityTransaction = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(trustProvider);
        _options = ValidateOptions(options ?? new GovernedModelUsageLedgerStoreOptions());
        _paths = new GovernedModelUsageLedgerStorePaths(paths);
        _workspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
        if (!ContextualRoleWorkspaceId.IsValid(_workspaceId))
        {
            throw new InvalidOperationException("The physical workspace did not produce a canonical workspace identity.");
        }
        _store = new AuthenticatedModelPersistenceStore<GovernedModelUsageLedgerStoreDocument>(
            paths.RootPath,
            _paths.PrimaryPath,
            _paths.ProofPath,
            _paths.LockPath,
            "embodysense-governed-model-usage-ledger-v1",
            _options.MaxArtifactUtf8Bytes,
            trustProvider,
            durabilityBarrier ?? NativeCapabilityCatalogDurabilityBarrier.Instance,
            _options.PathObserver,
            EmptyDocument,
            ValidateDocument,
            IsDirectSuccessor);
        _authorityTransaction = authorityTransaction ?? new CapabilityAuthorityTransaction(paths);
    }

    /// <inheritdoc />
    public async Task<GovernedModelUsageLedgerReadResult> ReadAsync(GovernedModelUsageLedgerIdentity identity, CancellationToken cancellationToken = default)
    {
        if (!IsLocalIdentity(identity))
        {
            return ReadResult(GovernedModelUsageLedgerReadStatus.Unavailable);
        }
        GovernedModelUsageLedgerReadResult? callbackResult = null;
        try
        {
            return await _authorityTransaction.ExecuteAsync(async token =>
            {
                callbackResult = await ReadCoreAsync(identity, token);
                return callbackResult;
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (callbackResult is null)
        {
            throw;
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception) || exception is OperationCanceledException)
        {
            return callbackResult ?? ReadResult(GovernedModelUsageLedgerReadStatus.Unavailable);
        }
    }

    /// <inheritdoc />
    public async Task<GovernedModelUsageLedgerRunReadResult> ReadRunAsync(string workspaceId, string runId, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(workspaceId, _workspaceId, StringComparison.Ordinal)
            || !ContextualRoleWorkspaceId.IsValid(workspaceId)
            || !IsArtifactIdentifier(runId))
        {
            return RunReadResult(GovernedModelUsageLedgerReadStatus.Unavailable);
        }

        GovernedModelUsageLedgerRunReadResult? callbackResult = null;
        try
        {
            return await _authorityTransaction.ExecuteAsync(async token =>
            {
                callbackResult = await ReadRunCoreAsync(runId, token);
                return callbackResult;
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (callbackResult is null)
        {
            throw;
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception) || exception is OperationCanceledException)
        {
            return callbackResult ?? RunReadResult(GovernedModelUsageLedgerReadStatus.Unavailable);
        }
    }

    /// <inheritdoc />
    public async Task<GovernedModelUsageReservationResult> ReserveAsync(GovernedModelUsageReservationRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsValidReservationRequest(request))
        {
            return ReservationResult(GovernedModelUsageLedgerAppendStatus.Conflict);
        }
        GovernedModelUsageReservationResult? callbackResult = null;
        try
        {
            return await _authorityTransaction.ExecuteAsync(async token =>
            {
                callbackResult = await ReserveCoreAsync(request, token, cancellationToken);
                return callbackResult;
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && callbackResult is null)
        {
            throw;
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception) || exception is OperationCanceledException)
        {
            return callbackResult ?? await AuthenticateReservationAsync(request).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<GovernedModelUsageLedgerAppendResult> AppendAsync(GovernedModelUsageLedgerEntry entry, long expectedGeneration, CancellationToken cancellationToken = default)
    {
        if (!IsLocalEntry(entry) || expectedGeneration < 1)
        {
            return AppendResult(GovernedModelUsageLedgerAppendStatus.Conflict);
        }
        GovernedModelUsageLedgerAppendResult? callbackResult = null;
        try
        {
            return await _authorityTransaction.ExecuteAsync(async token =>
            {
                callbackResult = await AppendCoreAsync(entry, expectedGeneration, token, cancellationToken);
                return callbackResult;
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && callbackResult is null)
        {
            throw;
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception) || exception is OperationCanceledException)
        {
            return callbackResult ?? await AuthenticateAppendAsync(entry).ConfigureAwait(false);
        }
    }

    private async Task<GovernedModelUsageLedgerReadResult> ReadCoreAsync(GovernedModelUsageLedgerIdentity identity, CancellationToken cancellationToken)
    {
        await using var session = await _store.AcquireForReadAsync(cancellationToken);
        if (session is null)
        {
            return ReadResult(GovernedModelUsageLedgerReadStatus.NotFound);
        }
        var loaded = await _store.LoadAsync(session, cancellationToken);
        var document = loaded.Disposition switch
        {
            AuthenticatedModelPersistenceDisposition.Current => loaded.Document,
            AuthenticatedModelPersistenceDisposition.Pending => loaded.Pending,
            _ => null
        };
        if (document is null)
        {
            return ReadResult(GovernedModelUsageLedgerReadStatus.Unavailable);
        }
        var entries = await ReadRunEntriesAsync(session, document, identity.RunId, cancellationToken);
        if (entries is null)
        {
            return ReadResult(GovernedModelUsageLedgerReadStatus.Unavailable);
        }
        var history = History(entries, identity);
        return history.Count == 0
            ? ReadResult(GovernedModelUsageLedgerReadStatus.NotFound)
            : new GovernedModelUsageLedgerReadResult(GovernedModelUsageLedgerReadStatus.Found, Array.AsReadOnly(history.ToArray()), history.Count);
    }

    private async Task<GovernedModelUsageLedgerRunReadResult> ReadRunCoreAsync(string runId, CancellationToken cancellationToken)
    {
        await using var session = await _store.AcquireForReadAsync(cancellationToken);
        if (session is null)
        {
            return RunReadResult(GovernedModelUsageLedgerReadStatus.NotFound);
        }
        var loaded = await _store.LoadAsync(session, cancellationToken);
        var document = loaded.Disposition switch
        {
            AuthenticatedModelPersistenceDisposition.Current => loaded.Document,
            AuthenticatedModelPersistenceDisposition.Pending => loaded.Pending,
            _ => null
        };
        if (document is null)
        {
            return RunReadResult(GovernedModelUsageLedgerReadStatus.Unavailable);
        }
        var entries = await ReadRunEntriesAsync(session, document, runId, cancellationToken);
        if (entries is null)
        {
            return RunReadResult(GovernedModelUsageLedgerReadStatus.Unavailable);
        }
        return entries.Length == 0
            ? RunReadResult(GovernedModelUsageLedgerReadStatus.NotFound, document.Generation)
            : new GovernedModelUsageLedgerRunReadResult(
                GovernedModelUsageLedgerReadStatus.Found,
                Array.AsReadOnly(entries),
                document.Generation);
    }

    private async Task<GovernedModelUsageReservationResult> ReserveCoreAsync(GovernedModelUsageReservationRequest request, CancellationToken cancellationToken, CancellationToken callerCancellationToken)
    {
        var mayHaveCommitted = false;
        try
        {
            await using var session = await _store.AcquireForMutationAsync(cancellationToken);
            var loaded = await LoadCurrentForMutationAsync(session, cancellationToken);
            if (loaded is null)
            {
                return ReservationResult(GovernedModelUsageLedgerAppendStatus.Unavailable);
            }
            var current = loaded.Document!;
            var runEntries = await ReadRunEntriesAsync(session, current, request.Identity.RunId, cancellationToken);
            if (runEntries is null)
            {
                return ReservationResult(GovernedModelUsageLedgerAppendStatus.Unavailable);
            }
            var operationEntries = runEntries.Where(value => string.Equals(value.Identity.AttemptOperationId, request.Identity.AttemptOperationId, StringComparison.Ordinal)).ToArray();
            if (operationEntries.Length > 0)
            {
                if (operationEntries.Any(value => !string.Equals(value.Identity.ContentHash, request.Identity.ContentHash, StringComparison.Ordinal)))
                {
                    return ReservationResult(GovernedModelUsageLedgerAppendStatus.Conflict);
                }
                var reservation = operationEntries.SingleOrDefault(value => value.Generation == 1);
                return reservation is not null && string.Equals(reservation.EvidenceHash, request.EvidenceHash, StringComparison.Ordinal)
                    ? ReservationResult(GovernedModelUsageLedgerAppendStatus.AlreadyPresent, operationEntries.Length, reservation)
                    : ReservationResult(GovernedModelUsageLedgerAppendStatus.Conflict);
            }
            if (!TryDeriveReservation(runEntries, request.Identity, request.BudgetPolicy, out var vector))
            {
                return ReservationResult(GovernedModelUsageLedgerAppendStatus.BudgetExhausted);
            }
            var reservationEntry = GovernedModelUsageLedgerEntry.Create(1, request.Identity, 1, GovernedModelUsageLedgerPhase.ReservationCommitted, vector, null, null, null, false, request.EvidenceHash, null, request.RecordedAtUtc);
            var rotate = current.Entries.Count >= _options.MaxEntries;
            if (rotate)
            {
                await ArchiveCurrentSegmentAsync(session, current, cancellationToken);
            }
            var candidate = CreateCandidate(current, reservationEntry, rotate);
            mayHaveCommitted = true;
            _ = await _store.CommitAsync(session, loaded, candidate, ObserveAsync, cancellationToken);
            return ReservationResult(GovernedModelUsageLedgerAppendStatus.Appended, 1, reservationEntry);
        }
        catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested && !mayHaveCommitted)
        {
            throw;
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception) || exception is OperationCanceledException)
        {
            return mayHaveCommitted ? await AuthenticateReservationAsync(request).ConfigureAwait(false) : ReservationResult(GovernedModelUsageLedgerAppendStatus.Unavailable);
        }
    }

    private async Task<GovernedModelUsageLedgerAppendResult> AppendCoreAsync(GovernedModelUsageLedgerEntry entry, long expectedGeneration, CancellationToken cancellationToken, CancellationToken callerCancellationToken)
    {
        var mayHaveCommitted = false;
        try
        {
            await using var session = await _store.AcquireForMutationAsync(cancellationToken);
            var loaded = await LoadCurrentForMutationAsync(session, cancellationToken);
            if (loaded is null)
            {
                return AppendResult(GovernedModelUsageLedgerAppendStatus.Unavailable);
            }
            var current = loaded.Document!;
            var runEntries = await ReadRunEntriesAsync(session, current, entry.Identity.RunId, cancellationToken);
            if (runEntries is null)
            {
                return AppendResult(GovernedModelUsageLedgerAppendStatus.Unavailable);
            }
            var operationEntries = runEntries.Where(value => string.Equals(value.Identity.AttemptOperationId, entry.Identity.AttemptOperationId, StringComparison.Ordinal)).ToArray();
            if (operationEntries.Any(value => !string.Equals(value.Identity.ContentHash, entry.Identity.ContentHash, StringComparison.Ordinal)))
            {
                return AppendResult(GovernedModelUsageLedgerAppendStatus.Conflict);
            }
            var history = History(runEntries, entry.Identity);
            if (entry.Generation <= history.Count)
            {
                var retained = history[checked((int)entry.Generation - 1)];
                return AppendResult(string.Equals(retained.ContentHash, entry.ContentHash, StringComparison.Ordinal) ? GovernedModelUsageLedgerAppendStatus.AlreadyPresent : GovernedModelUsageLedgerAppendStatus.Conflict, history.Count);
            }
            if (expectedGeneration != history.Count || entry.Generation != history.Count + 1 || history.Count == 0)
            {
                return AppendResult(GovernedModelUsageLedgerAppendStatus.Conflict, history.Count);
            }
            if (history.Count >= _options.MaxEntriesPerAttempt)
            {
                return AppendResult(GovernedModelUsageLedgerAppendStatus.Unavailable, history.Count);
            }
            var candidateHistory = history.Append(entry).ToArray();
            if (!GovernedModelUsageLedgerHistoryValidator.IsValid(candidateHistory, entry.Identity, candidateHistory.Length))
            {
                return AppendResult(GovernedModelUsageLedgerAppendStatus.Conflict, history.Count);
            }
            var rotate = current.Entries.Count >= _options.MaxEntries;
            if (rotate)
            {
                await ArchiveCurrentSegmentAsync(session, current, cancellationToken);
            }
            var candidate = CreateCandidate(current, entry, rotate);
            mayHaveCommitted = true;
            _ = await _store.CommitAsync(session, loaded, candidate, ObserveAsync, cancellationToken);
            return AppendResult(GovernedModelUsageLedgerAppendStatus.Appended, history.Count + 1);
        }
        catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested && !mayHaveCommitted)
        {
            throw;
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception) || exception is OperationCanceledException)
        {
            return mayHaveCommitted ? await AuthenticateAppendAsync(entry).ConfigureAwait(false) : AppendResult(GovernedModelUsageLedgerAppendStatus.Unavailable);
        }
    }

    private async Task<AuthenticatedModelPersistenceLoadResult<GovernedModelUsageLedgerStoreDocument>?> LoadCurrentForMutationAsync(CapabilityCatalogPathSession session, CancellationToken cancellationToken)
    {
        var loaded = await _store.LoadAsync(session, cancellationToken);
        if (loaded.Disposition == AuthenticatedModelPersistenceDisposition.Pending)
        {
            _ = await _store.FinalizePendingAsync(loaded, cancellationToken);
            loaded = await _store.LoadAsync(session, cancellationToken);
        }
        return loaded.Disposition == AuthenticatedModelPersistenceDisposition.Current && loaded.Document is not null ? loaded : null;
    }

    private Task ArchiveCurrentSegmentAsync(CapabilityCatalogPathSession session, GovernedModelUsageLedgerStoreDocument current, CancellationToken cancellationToken)
    {
        session.PrepareDirectory(_paths.SegmentRootPath);
        return _store.WriteAuthenticatedSnapshotOnceAsync(session, current, _paths.SegmentPath(current.SegmentIndex), cancellationToken);
    }

    private async Task<GovernedModelUsageLedgerEntry[]?> ReadRunEntriesAsync(CapabilityCatalogPathSession session, GovernedModelUsageLedgerStoreDocument current, string runId, CancellationToken cancellationToken)
    {
        var documents = new List<GovernedModelUsageLedgerStoreDocument> { current };
        var expectedDigest = current.PreviousSegmentContentDigest;
        var expectedGeneration = current.SegmentStartGeneration - 1;
        for (var segmentIndex = current.SegmentIndex - 1; segmentIndex >= 0; segmentIndex--)
        {
            var archived = await _store.TryReadAuthenticatedSnapshotAsync(session, _paths.SegmentPath(segmentIndex), cancellationToken);
            if (archived is null
                || archived.SegmentIndex != segmentIndex
                || archived.Generation != expectedGeneration
                || !string.Equals(archived.ContentDigest, expectedDigest, StringComparison.Ordinal))
            {
                return null;
            }
            documents.Add(archived);
            expectedDigest = archived.PreviousSegmentContentDigest;
            expectedGeneration = archived.SegmentStartGeneration - 1;
        }
        if (expectedDigest is not null || expectedGeneration != 0)
        {
            return null;
        }

        documents.Reverse();
        var entries = documents
            .SelectMany(document => document.Entries)
            .Where(entry => string.Equals(entry.Identity.RunId, runId, StringComparison.Ordinal))
            .ToArray();
        return ValidateRunEntries(entries, runId) ? entries : null;
    }

    private async Task<GovernedModelUsageReservationResult> AuthenticateReservationAsync(GovernedModelUsageReservationRequest request)
    {
        try
        {
            var read = await ReadAsync(request.Identity, CancellationToken.None);
            if (read.Status != GovernedModelUsageLedgerReadStatus.Found)
            {
                return ReservationResult(GovernedModelUsageLedgerAppendStatus.Unavailable);
            }
            var reservation = read.Entries[0];
            return string.Equals(reservation.EvidenceHash, request.EvidenceHash, StringComparison.Ordinal)
                ? ReservationResult(GovernedModelUsageLedgerAppendStatus.Appended, read.Generation, reservation)
                : ReservationResult(GovernedModelUsageLedgerAppendStatus.Conflict);
        }
        catch
        {
            return ReservationResult(GovernedModelUsageLedgerAppendStatus.Unavailable);
        }
    }

    private async Task<GovernedModelUsageLedgerAppendResult> AuthenticateAppendAsync(GovernedModelUsageLedgerEntry entry)
    {
        try
        {
            var read = await ReadAsync(entry.Identity, CancellationToken.None);
            if (read.Status != GovernedModelUsageLedgerReadStatus.Found || entry.Generation > read.Entries.Count)
            {
                return AppendResult(GovernedModelUsageLedgerAppendStatus.Unavailable);
            }
            return AppendResult(string.Equals(read.Entries[checked((int)entry.Generation - 1)].ContentHash, entry.ContentHash, StringComparison.Ordinal) ? GovernedModelUsageLedgerAppendStatus.Appended : GovernedModelUsageLedgerAppendStatus.Conflict, read.Generation);
        }
        catch
        {
            return AppendResult(GovernedModelUsageLedgerAppendStatus.Unavailable);
        }
    }

    private static bool TryDeriveReservation(IReadOnlyList<GovernedModelUsageLedgerEntry> entries, GovernedModelUsageLedgerIdentity identity, GovernedModelBudgetPolicy policy, out GovernedModelUsageCeiling reservation)
    {
        reservation = null!;
        var histories = entries.GroupBy(entry => entry.Identity.ContentHash, StringComparer.Ordinal).Select(group => group.OrderBy(entry => entry.Generation).ToArray()).ToArray();
        var nodeConsumption = Aggregate(histories.Where(history => SameNodeSeries(history[0].Identity, identity)));
        var runConsumption = Aggregate(histories.Where(history => SameRun(history[0].Identity, identity)));
        if (nodeConsumption is null || runConsumption is null)
        {
            return false;
        }

        if (!TryReserve(policy.PerAttempt.InputTokens, policy.PerNodeSeries.InputTokens, policy.PerRun.InputTokens, nodeConsumption.InputTokens, runConsumption.InputTokens, out var input)
            || !TryReserve(policy.PerAttempt.OutputTokens, policy.PerNodeSeries.OutputTokens, policy.PerRun.OutputTokens, nodeConsumption.OutputTokens, runConsumption.OutputTokens, out var output)
            || !TryReserve(policy.PerAttempt.CachedTokens, policy.PerNodeSeries.CachedTokens, policy.PerRun.CachedTokens, nodeConsumption.CachedTokens, runConsumption.CachedTokens, out var cached)
            || !TryReserve(policy.PerAttempt.TotalTokens, policy.PerNodeSeries.TotalTokens, policy.PerRun.TotalTokens, nodeConsumption.TotalTokens, runConsumption.TotalTokens, out var total)
            || !TryReserveMonetary(policy, nodeConsumption, runConsumption, out var monetary))
        {
            return false;
        }
        reservation = GovernedModelUsageCeiling.Create(input, output, cached, total, monetary);
        return true;
    }

    private static GovernedModelUsageVector? Aggregate(IEnumerable<GovernedModelUsageLedgerEntry[]> histories)
    {
        long input = 0;
        long output = 0;
        long cached = 0;
        long total = 0;
        long cost = 0;
        string? currency = null;
        try
        {
            foreach (var history in histories)
            {
                var effective = EffectiveConsumption(history);
                input = checked(input + effective.InputTokens);
                output = checked(output + effective.OutputTokens);
                cached = checked(cached + effective.CachedTokens);
                total = checked(total + effective.TotalTokens);
                cost = checked(cost + effective.CostMicros);
                if (effective.CostMicros > 0)
                {
                    if (currency is not null && !string.Equals(currency, effective.Currency, StringComparison.Ordinal))
                    {
                        return null;
                    }
                    currency = effective.Currency;
                }
            }
            return GovernedModelUsageVector.Create(input, output, cached, total, currency, cost);
        }
        catch (Exception exception) when (exception is OverflowException or ArgumentException)
        {
            return null;
        }
    }

    private static GovernedModelUsageVector EffectiveConsumption(IReadOnlyList<GovernedModelUsageLedgerEntry> history)
    {
        var reservation = history[0].Reservation!;
        if (history.Any(entry => entry.Phase == GovernedModelUsageLedgerPhase.DispatchProvedNotStarted))
        {
            return GovernedModelUsageVector.Zero;
        }
        var latestRelease = history.LastOrDefault(entry => entry.Released is not null)?.Released;
        var latestUsed = history.LastOrDefault(entry => entry.Used is not null)?.Used;
        var retainedCost = reservation.MonetaryCost.IsBounded
            ? Math.Max(0, reservation.MonetaryCost.MaximumMicros - (latestRelease?.CostMicros ?? 0))
            : 0;
        var effectiveCost = Math.Max(retainedCost, latestUsed?.CostMicros ?? 0);
        var effectiveCurrency = effectiveCost == 0
            ? null
            : latestUsed?.Currency is not null
                ? latestUsed.Currency
                : reservation.MonetaryCost.Currency;
        return GovernedModelUsageVector.Create(
            EffectiveDimension(reservation.InputTokens, latestRelease?.InputTokens ?? 0, latestUsed?.InputTokens ?? 0),
            EffectiveDimension(reservation.OutputTokens, latestRelease?.OutputTokens ?? 0, latestUsed?.OutputTokens ?? 0),
            EffectiveDimension(reservation.CachedTokens, latestRelease?.CachedTokens ?? 0, latestUsed?.CachedTokens ?? 0),
            EffectiveDimension(reservation.TotalTokens, latestRelease?.TotalTokens ?? 0, latestUsed?.TotalTokens ?? 0),
            effectiveCurrency,
            effectiveCost);
    }

    private static long EffectiveDimension(GovernedModelUsageLimit reservation, long released, long used)
        => Math.Max(reservation.IsBounded ? Math.Max(0, reservation.Maximum - released) : 0, used);

    private static bool TryReserve(GovernedModelUsageLimit attempt, GovernedModelUsageLimit node, GovernedModelUsageLimit run, long nodeConsumed, long runConsumed, out GovernedModelUsageLimit reservation)
    {
        var candidates = new List<long>(3);
        if (attempt.IsBounded) candidates.Add(attempt.Maximum);
        if (node.IsBounded) candidates.Add(node.Maximum - nodeConsumed);
        if (run.IsBounded) candidates.Add(run.Maximum - runConsumed);
        if (candidates.Count == 0)
        {
            reservation = GovernedModelUsageLimit.Unbounded;
            return true;
        }
        var amount = candidates.Min();
        reservation = amount > 0 ? GovernedModelUsageLimit.Bounded(amount) : GovernedModelUsageLimit.Unbounded;
        return amount > 0;
    }

    private static bool TryReserveMonetary(GovernedModelBudgetPolicy policy, GovernedModelUsageVector nodeConsumed, GovernedModelUsageVector runConsumed, out GovernedModelMonetaryLimit reservation)
    {
        var limits = new[] { policy.PerAttempt.MonetaryCost, policy.PerNodeSeries.MonetaryCost, policy.PerRun.MonetaryCost };
        var bounded = limits.Where(limit => limit.IsBounded).ToArray();
        if (bounded.Length == 0)
        {
            reservation = GovernedModelMonetaryLimit.Unbounded;
            return true;
        }
        var currency = bounded[0].Currency!;
        if (nodeConsumed.CostMicros > 0 && !string.Equals(nodeConsumed.Currency, currency, StringComparison.Ordinal)
            || runConsumed.CostMicros > 0 && !string.Equals(runConsumed.Currency, currency, StringComparison.Ordinal))
        {
            reservation = GovernedModelMonetaryLimit.Unbounded;
            return false;
        }
        var candidates = new List<long>(3);
        if (policy.PerAttempt.MonetaryCost.IsBounded) candidates.Add(policy.PerAttempt.MonetaryCost.MaximumMicros);
        if (policy.PerNodeSeries.MonetaryCost.IsBounded) candidates.Add(policy.PerNodeSeries.MonetaryCost.MaximumMicros - nodeConsumed.CostMicros);
        if (policy.PerRun.MonetaryCost.IsBounded) candidates.Add(policy.PerRun.MonetaryCost.MaximumMicros - runConsumed.CostMicros);
        var amount = candidates.Min();
        reservation = amount > 0 ? GovernedModelMonetaryLimit.Bounded(currency, amount) : GovernedModelMonetaryLimit.Unbounded;
        return amount > 0;
    }

    private static bool SameNodeSeries(GovernedModelUsageLedgerIdentity left, GovernedModelUsageLedgerIdentity right)
        => SameRun(left, right) && string.Equals(left.NodeId, right.NodeId, StringComparison.Ordinal);

    private static bool SameRun(GovernedModelUsageLedgerIdentity left, GovernedModelUsageLedgerIdentity right)
        => string.Equals(left.WorkspaceId, right.WorkspaceId, StringComparison.Ordinal)
            && string.Equals(left.RunId, right.RunId, StringComparison.Ordinal)
            && left.ExecutionGeneration == right.ExecutionGeneration
            && string.Equals(left.GraphId, right.GraphId, StringComparison.Ordinal)
            && string.Equals(left.GraphRevisionId, right.GraphRevisionId, StringComparison.Ordinal)
            && string.Equals(left.GraphExecutableHash, right.GraphExecutableHash, StringComparison.Ordinal)
            && string.Equals(left.AdmissionReceiptHash, right.AdmissionReceiptHash, StringComparison.Ordinal)
            && string.Equals(left.RoutingAdmissionHash, right.RoutingAdmissionHash, StringComparison.Ordinal);

    private GovernedModelUsageLedgerStoreDocument EmptyDocument(string workspaceIdentity)
        => new(1, workspaceIdentity, _workspaceId, 0, 0, 1, null, [], string.Empty, string.Empty);

    private static GovernedModelUsageLedgerStoreDocument CreateCandidate(GovernedModelUsageLedgerStoreDocument current, GovernedModelUsageLedgerEntry entry, bool rotate)
        => rotate
            ? new(1, current.WorkspaceIdentity, current.WorkspaceId, checked(current.Generation + 1), checked(current.SegmentIndex + 1), checked(current.Generation + 1), current.ContentDigest, [entry], string.Empty, string.Empty)
            : new(1, current.WorkspaceIdentity, current.WorkspaceId, checked(current.Generation + 1), current.SegmentIndex, current.SegmentStartGeneration, current.PreviousSegmentContentDigest, current.Entries.Append(entry).ToArray(), string.Empty, string.Empty);

    private bool ValidateDocument(GovernedModelUsageLedgerStoreDocument document, string workspaceIdentity)
    {
        try
        {
            if (document.SchemaVersion != 1 || !string.Equals(document.WorkspaceIdentity, workspaceIdentity, StringComparison.Ordinal)
                || !string.Equals(document.WorkspaceId, _workspaceId, StringComparison.Ordinal) || document.Generation < 0
                || document.SegmentIndex < 0 || document.SegmentStartGeneration < 1
                || document.Entries is null || document.Entries.Count > _options.MaxEntries
                || document.Generation != document.SegmentStartGeneration + document.Entries.Count - 1
                || (document.SegmentIndex == 0) != (document.PreviousSegmentContentDigest is null)
                || document.PreviousSegmentContentDigest is not null && !CapabilityIntegrityDigest.TryParse(document.PreviousSegmentContentDigest, out _, out _))
            {
                return false;
            }
            var operationIdentities = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in document.Entries)
            {
                if (!IsLocalEntry(entry))
                {
                    return false;
                }
                if (operationIdentities.TryGetValue(entry.Identity.AttemptOperationId, out var identityHash) && !string.Equals(identityHash, entry.Identity.ContentHash, StringComparison.Ordinal))
                {
                    return false;
                }
                operationIdentities[entry.Identity.AttemptOperationId] = entry.Identity.ContentHash;
            }
            foreach (var history in document.Entries.GroupBy(entry => entry.Identity.ContentHash, StringComparer.Ordinal).Select(group => group.ToArray()))
            {
                if (history.Length is < 1 || history.Length > _options.MaxEntriesPerAttempt)
                {
                    return false;
                }
                if (history[0].Generation == 1)
                {
                    if (!GovernedModelUsageLedgerHistoryValidator.IsValid(history, history[0].Identity, history.Length))
                    {
                        return false;
                    }
                }
                else
                {
                    for (var index = 1; index < history.Length; index++)
                    {
                        if (history[index].Generation != history[index - 1].Generation + 1
                            || !string.Equals(history[index].PreviousEntryHash, history[index - 1].ContentHash, StringComparison.Ordinal))
                        {
                            return false;
                        }
                    }
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool IsDirectSuccessor(GovernedModelUsageLedgerStoreDocument current, GovernedModelUsageLedgerStoreDocument candidate)
        => candidate.Generation == current.Generation + 1
            && candidate.SchemaVersion == current.SchemaVersion
            && string.Equals(candidate.WorkspaceIdentity, current.WorkspaceIdentity, StringComparison.Ordinal)
            && string.Equals(candidate.WorkspaceId, current.WorkspaceId, StringComparison.Ordinal)
            && (candidate.SegmentIndex == current.SegmentIndex
                && current.Entries.Count < _options.MaxEntries
                && candidate.SegmentStartGeneration == current.SegmentStartGeneration
                && string.Equals(candidate.PreviousSegmentContentDigest, current.PreviousSegmentContentDigest, StringComparison.Ordinal)
                && candidate.Entries.Count == current.Entries.Count + 1
                && candidate.Entries.Take(current.Entries.Count).Zip(current.Entries).All(pair => string.Equals(pair.First.ContentHash, pair.Second.ContentHash, StringComparison.Ordinal))
                || candidate.SegmentIndex == current.SegmentIndex + 1
                && current.Entries.Count == _options.MaxEntries
                && candidate.SegmentStartGeneration == candidate.Generation
                && string.Equals(candidate.PreviousSegmentContentDigest, current.ContentDigest, StringComparison.Ordinal)
                && candidate.Entries.Count == 1);

    private static IReadOnlyList<GovernedModelUsageLedgerEntry> History(IReadOnlyList<GovernedModelUsageLedgerEntry> entries, GovernedModelUsageLedgerIdentity identity)
        => entries.Where(entry => string.Equals(entry.Identity.ContentHash, identity.ContentHash, StringComparison.Ordinal)).OrderBy(entry => entry.Generation).ToArray();

    private bool ValidateRunEntries(IReadOnlyList<GovernedModelUsageLedgerEntry> entries, string runId)
    {
        var operationIdentities = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!IsLocalEntry(entry) || !string.Equals(entry.Identity.RunId, runId, StringComparison.Ordinal))
            {
                return false;
            }
            if (operationIdentities.TryGetValue(entry.Identity.AttemptOperationId, out var identityHash)
                && !string.Equals(identityHash, entry.Identity.ContentHash, StringComparison.Ordinal))
            {
                return false;
            }
            operationIdentities[entry.Identity.AttemptOperationId] = entry.Identity.ContentHash;
        }
        foreach (var history in entries.GroupBy(entry => entry.Identity.ContentHash, StringComparer.Ordinal).Select(group => group.OrderBy(entry => entry.Generation).ToArray()))
        {
            if (history.Length is < 1 || history.Length > _options.MaxEntriesPerAttempt
                || !GovernedModelUsageLedgerHistoryValidator.IsValid(history, history[0].Identity, history.Length))
            {
                return false;
            }
        }
        return true;
    }

    private bool IsLocalIdentity(GovernedModelUsageLedgerIdentity? identity)
        => GovernedModelContractValidator.IsValid(identity) && string.Equals(identity!.WorkspaceId, _workspaceId, StringComparison.Ordinal);

    private bool IsLocalEntry(GovernedModelUsageLedgerEntry? entry)
        => GovernedModelContractValidator.IsValid(entry) && IsLocalIdentity(entry!.Identity);

    private bool IsValidReservationRequest(GovernedModelUsageReservationRequest? request)
        => request is not null && IsLocalIdentity(request.Identity) && GovernedModelContractValidator.IsValid(request.BudgetPolicy)
            && string.Equals(request.Identity.BudgetPolicyHash, request.BudgetPolicy.ContentHash, StringComparison.Ordinal)
            && IsHash(request.EvidenceHash) && request.RecordedAtUtc != default && request.RecordedAtUtc.Offset == TimeSpan.Zero;

    private static bool IsArtifactIdentifier(string? value)
    {
        try
        {
            CustomLoopArtifactIdentifier.Require(value!, nameof(value), GovernedLoopExecutionLimits.MaxIdentifierCharacters);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static GovernedModelUsageLedgerStoreOptions ValidateOptions(GovernedModelUsageLedgerStoreOptions options)
    {
        if (options.MaxEntries is < 1 or > GovernedModelUsageLedgerStoreOptions.MaximumEntries
            || options.MaxEntriesPerAttempt is < 1 or > GovernedModelUsageLedgerStoreOptions.MaximumEntriesPerAttempt
            || options.MaxArtifactUtf8Bytes is < 1 or > GovernedModelUsageLedgerStoreOptions.MaximumArtifactUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Model-usage ledger options must remain within schema-1 bounds.");
        }
        return options;
    }

    private ValueTask ObserveAsync(AuthenticatedModelPersistenceCommitStage stage, CancellationToken cancellationToken)
        => _options.DurableBoundaryObserver is null
            ? ValueTask.CompletedTask
            : _options.DurableBoundaryObserver((GovernedModelUsageLedgerPersistenceBoundary)(int)stage, cancellationToken);

    private static GovernedModelUsageLedgerReadResult ReadResult(GovernedModelUsageLedgerReadStatus status) => new(status, [], 0);

    private static GovernedModelUsageLedgerRunReadResult RunReadResult(GovernedModelUsageLedgerReadStatus status, long workspaceGeneration = 0)
        => new(status, [], workspaceGeneration);

    private static GovernedModelUsageReservationResult ReservationResult(GovernedModelUsageLedgerAppendStatus status, long generation = 0, GovernedModelUsageLedgerEntry? entry = null) => new(status, generation, entry);

    private static GovernedModelUsageLedgerAppendResult AppendResult(GovernedModelUsageLedgerAppendStatus status, long generation = 0) => new(status, generation);

    private static bool IsAvailabilityFailure(Exception exception)
        => exception is IOException or UnauthorizedAccessException or JsonException or FormatException or OverflowException or CryptographicException or InvalidOperationException or AuthenticatedModelPersistenceLimitException;
}
