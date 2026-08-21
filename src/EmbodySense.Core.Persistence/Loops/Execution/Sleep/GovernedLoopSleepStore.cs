using System.ComponentModel;
using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Application.Loops.Posture;
using EmbodySense.Core.Application.Loops.Posture.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Posture;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Persistence.Triggers;
using EmbodySense.Core.Persistence.Triggers.Models;

namespace EmbodySense.Core.Persistence.Loops.Execution.Sleep;

/// <summary>Persists immutable sleeping checkpoints and exactly-once wake-evidence transitions in a workspace-scoped schema-1 ledger.</summary>
/// <remarks>
/// Mutations are serialized across processes and publish complete immutable generations. The immutable checkpoint retains
/// its publication posture hash while a later wake claim retains the independently resolved current-posture hash that
/// admitted that claim. Each claimed wake retains its bounded append-only transition chain while reads expose the current
/// terminal or reconcilable head. Cancellation is honored through the last check before staging, after which the durable
/// publication protocol runs to a conclusive or explicitly ambiguous boundary.
/// </remarks>
public sealed class GovernedLoopSleepStore : IGovernedLoopSleepStore, IGovernedLoopWakeOperationalPosturePort
{
    private const int SchemaVersion = 1;
    private const int MaximumConfiguredCheckpoints = 16_384;
    private const int MaximumConfiguredCatalogBytes = 128 * 1024 * 1024;
    private const int MaximumConfiguredDurabilityArtifacts = 64;
    private readonly TriggerQueueArtifactGuard _guard;
    private readonly int _maximumCatalogBytes;
    private readonly int _maximumCheckpoints;
    private readonly ITriggerQueueDurabilityObserver _observer;

    /// <summary>Creates a bounded workspace sleep store.</summary>
    /// <param name="paths">The workspace paths that own the local ledger.</param>
    /// <param name="options">Optional finite storage and crash-observation settings.</param>
    public GovernedLoopSleepStore(WorkspacePaths paths, GovernedLoopSleepStoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        options ??= new GovernedLoopSleepStoreOptions();
        ValidateOptions(options);
        _maximumCheckpoints = options.MaxCheckpoints;
        _maximumCatalogBytes = options.MaxCatalogUtf8Bytes;
        _observer = new GovernedLoopSleepStoreDurabilityObserver(options.DurableBoundaryObserver);
        var storeRoot = paths.AgentFile(Path.Combine("loops", "execution", "sleep"));
        _guard = new TriggerQueueArtifactGuard(paths.RootPath, storeRoot, options.MaxDurabilityArtifacts, recycleAuthenticatedTombstones: true);
    }

    /// <inheritdoc />
    public async Task<GovernedLoopSleepCheckpointMutationResult?> PublishAndReleaseAsync(
        GovernedLoopSleepCheckpoint checkpoint,
        string expectedPostureHash,
        CancellationToken cancellationToken = default)
    {
        if (!TryCaptureCheckpoint(checkpoint, expectedPostureHash, out var captured))
        {
            return CheckpointMutation(GovernedLoopSleepCheckpointMutationStatus.Conflict);
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var proposed = captured!;
            using var mutationLock = await _guard.AcquireMutationLockAsync(_observer, cancellationToken).ConfigureAwait(false);
            var (catalog, identity) = await LoadAsync(mutationLock, cancellationToken).ConfigureAwait(false);
            var existing = catalog.Entries.SingleOrDefault(entry => string.Equals(entry.Checkpoint.CheckpointId, proposed.CheckpointId, StringComparison.Ordinal));
            if (existing is not null)
            {
                var exact = SameCheckpointIdentity(proposed, existing.Checkpoint)
                    && string.Equals(existing.PublicationPostureHash, expectedPostureHash, StringComparison.Ordinal);
                return CheckpointMutation(
                    exact ? GovernedLoopSleepCheckpointMutationStatus.Replayed : GovernedLoopSleepCheckpointMutationStatus.Conflict,
                    exact ? existing.Checkpoint : null);
            }

            if (catalog.Entries.Count >= _maximumCheckpoints)
            {
                return CheckpointMutation(GovernedLoopSleepCheckpointMutationStatus.Conflict);
            }

            var candidate = new GovernedLoopSleepStoreCatalog(
                SchemaVersion,
                checked(catalog.Generation + 1),
                Ordered(catalog.Entries.Append(new GovernedLoopSleepStoreEntry(proposed, expectedPostureHash, null, []))));
            await WriteAsync(candidate, identity, mutationLock, cancellationToken).ConfigureAwait(false);
            return CheckpointMutation(GovernedLoopSleepCheckpointMutationStatus.Committed, proposed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (GovernedLoopSleepStoreBoundaryObserverException)
        {
            return CheckpointMutation(GovernedLoopSleepCheckpointMutationStatus.Ambiguous);
        }
        catch (Exception exception) when (IsConflict(exception))
        {
            return CheckpointMutation(GovernedLoopSleepCheckpointMutationStatus.Conflict);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return CheckpointMutation(GovernedLoopSleepCheckpointMutationStatus.Unavailable);
        }
    }

    /// <inheritdoc />
    public async Task<GovernedLoopSleepCheckpointReadResult?> ReadCheckpointAsync(string checkpointId, CancellationToken cancellationToken = default)
    {
        if (!IsHash(checkpointId))
        {
            return CheckpointRead(GovernedLoopSleepStoreReadStatus.Conflict);
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var mutationLock = await _guard.AcquireMutationLockAsync(_observer, cancellationToken).ConfigureAwait(false);
            var (catalog, _) = await LoadAsync(mutationLock, cancellationToken).ConfigureAwait(false);
            var existing = catalog.Entries.SingleOrDefault(entry => string.Equals(entry.Checkpoint.CheckpointId, checkpointId, StringComparison.Ordinal));
            return existing is null
                ? CheckpointRead(GovernedLoopSleepStoreReadStatus.NotFound)
                : CheckpointRead(GovernedLoopSleepStoreReadStatus.Found, existing.Checkpoint);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsConflict(exception))
        {
            return CheckpointRead(GovernedLoopSleepStoreReadStatus.Conflict);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return CheckpointRead(GovernedLoopSleepStoreReadStatus.Unavailable);
        }
    }

    /// <inheritdoc />
    public async Task<GovernedLoopWakeEvidenceReadResult?> ReadWakeAsync(string wakeId, CancellationToken cancellationToken = default)
    {
        if (!IsHash(wakeId))
        {
            return WakeRead(GovernedLoopSleepStoreReadStatus.Conflict);
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var mutationLock = await _guard.AcquireMutationLockAsync(_observer, cancellationToken).ConfigureAwait(false);
            var (catalog, _) = await LoadAsync(mutationLock, cancellationToken).ConfigureAwait(false);
            var entry = catalog.Entries
                .SingleOrDefault(candidate => candidate.WakeEvidence.Any(evidence => string.Equals(evidence.Identity.WakeId, wakeId, StringComparison.Ordinal)));
            var evidence = entry?.WakeEvidence[^1];
            var prepared = entry?.WakeEvidence.SingleOrDefault(candidate => candidate.Disposition == GovernedLoopWakeDisposition.Prepared);
            return evidence is null
                ? WakeRead(GovernedLoopSleepStoreReadStatus.NotFound)
                : WakeRead(GovernedLoopSleepStoreReadStatus.Found, evidence, prepared);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsConflict(exception))
        {
            return WakeRead(GovernedLoopSleepStoreReadStatus.Conflict);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return WakeRead(GovernedLoopSleepStoreReadStatus.Unavailable);
        }
    }

    /// <inheritdoc />
    public async Task<GovernedLoopWakeCatalogEvidenceReadResult> ReadAsync(
        GovernedLoopOperationalEvidencePageRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null
            || request.MaximumCount is < 1 or > GovernedLoopOperationalPostureLimits.MaxPageItems
            || request.AfterId is not null
                && !CustomLoopArtifactIdentifier.IsValid(request.AfterId, GovernedLoopOperationalPostureLimits.MaxTargetIdCharacters))
        {
            return Operational(GovernedLoopOperationalEvidenceReadStatus.Corrupt);
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var mutationLock = await _guard.AcquireMutationLockAsync(_observer, cancellationToken).ConfigureAwait(false);
            var (catalog, _) = await LoadAsync(mutationLock, cancellationToken).ConfigureAwait(false);
            var selected = catalog.Entries
                .Where(entry => request.AfterId is null || string.Compare(entry.Checkpoint.CheckpointId, request.AfterId, StringComparison.Ordinal) > 0)
                .OrderBy(entry => entry.Checkpoint.CheckpointId, StringComparer.Ordinal)
                .Take(request.MaximumCount + 1)
                .ToArray();
            var hasMore = selected.Length > request.MaximumCount;
            var items = Array.AsReadOnly(selected
                .Take(request.MaximumCount)
                .Select(entry => new GovernedLoopWakeEvidenceSnapshot(
                    Copy(entry.Checkpoint),
                    entry.WakeEvidence.Count == 0 ? null : Copy(entry.WakeEvidence[^1])))
                .ToArray());
            return new GovernedLoopWakeCatalogEvidenceReadResult(
                items.Count == 0 ? GovernedLoopOperationalEvidenceReadStatus.Empty : GovernedLoopOperationalEvidenceReadStatus.Found,
                catalog.Generation,
                hasMore,
                hasMore ? items[^1].Checkpoint.CheckpointId : null,
                items);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is GovernedLoopSleepStoreLimitException or TriggerQueuePersistenceBackpressureException)
        {
            return Operational(GovernedLoopOperationalEvidenceReadStatus.Backpressured);
        }
        catch (Exception exception) when (exception is FormatException or InvalidDataException or OverflowException or ArgumentException or Win32Exception)
        {
            return Operational(GovernedLoopOperationalEvidenceReadStatus.Corrupt);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or PlatformNotSupportedException or NotSupportedException)
        {
            return Operational(GovernedLoopOperationalEvidenceReadStatus.Unavailable);
        }
    }

    /// <inheritdoc />
    public async Task<GovernedLoopWakeEvidenceMutationResult?> CreateWakeAsync(
        GovernedLoopSleepCheckpoint checkpoint,
        GovernedLoopWakeEvidence evidence,
        string wakeClaimPostureHash,
        CancellationToken cancellationToken = default)
    {
        if (!TryCaptureCheckpoint(checkpoint, wakeClaimPostureHash, out var capturedCheckpoint)
            || !TryCaptureWake(evidence, out var capturedWake)
            || capturedWake!.EvidenceVersion != 1
            || !GovernedLoopSleepContractValidator.ValidateComposition(capturedCheckpoint, capturedWake).IsValid)
        {
            return WakeMutation(GovernedLoopWakeEvidenceMutationStatus.Conflict);
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var mutationLock = await _guard.AcquireMutationLockAsync(_observer, cancellationToken).ConfigureAwait(false);
            var (catalog, identity) = await LoadAsync(mutationLock, cancellationToken).ConfigureAwait(false);
            var existing = catalog.Entries.SingleOrDefault(entry => string.Equals(entry.Checkpoint.CheckpointId, capturedCheckpoint!.CheckpointId, StringComparison.Ordinal));
            if (existing is null
                || !string.Equals(existing.Checkpoint.ContentHash, capturedCheckpoint!.ContentHash, StringComparison.Ordinal))
            {
                return WakeMutation(GovernedLoopWakeEvidenceMutationStatus.Conflict);
            }

            if (existing.WakeEvidence.Count > 0)
            {
                var currentWake = existing.WakeEvidence[^1];
                var sameWake = string.Equals(currentWake.Identity.WakeId, capturedWake.Identity.WakeId, StringComparison.Ordinal);
                var sameInitialEvidence = sameWake
                    && string.Equals(existing.WakeEvidence[0].ContentHash, capturedWake.ContentHash, StringComparison.Ordinal);
                return WakeMutation(
                    sameInitialEvidence && string.Equals(existing.WakeClaimPostureHash, wakeClaimPostureHash, StringComparison.Ordinal)
                        ? GovernedLoopWakeEvidenceMutationStatus.Replayed
                        : sameWake
                            ? GovernedLoopWakeEvidenceMutationStatus.Conflict
                            : GovernedLoopWakeEvidenceMutationStatus.CheckpointClaimed,
                    currentWake);
            }

            var replacement = existing with
            {
                WakeClaimPostureHash = wakeClaimPostureHash,
                WakeEvidence = Array.AsReadOnly(new[] { capturedWake })
            };
            var candidate = Replace(catalog, existing, replacement);
            await WriteAsync(candidate, identity, mutationLock, cancellationToken).ConfigureAwait(false);
            return WakeMutation(GovernedLoopWakeEvidenceMutationStatus.Committed, capturedWake);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (GovernedLoopSleepStoreBoundaryObserverException)
        {
            return WakeMutation(GovernedLoopWakeEvidenceMutationStatus.Ambiguous);
        }
        catch (Exception exception) when (IsConflict(exception))
        {
            return WakeMutation(GovernedLoopWakeEvidenceMutationStatus.Conflict);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return WakeMutation(GovernedLoopWakeEvidenceMutationStatus.Unavailable);
        }
    }

    /// <inheritdoc />
    public async Task<GovernedLoopWakeEvidenceMutationResult?> AdvanceWakeAsync(
        GovernedLoopWakeEvidence current,
        GovernedLoopWakeEvidence next,
        CancellationToken cancellationToken = default)
    {
        if (!TryCaptureWake(current, out var capturedCurrent)
            || !TryCaptureWake(next, out var capturedNext)
            || !GovernedLoopSleepContractValidator.ValidateTransition(capturedCurrent, capturedNext).IsValid)
        {
            return WakeMutation(GovernedLoopWakeEvidenceMutationStatus.Conflict);
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var mutationLock = await _guard.AcquireMutationLockAsync(_observer, cancellationToken).ConfigureAwait(false);
            var (catalog, identity) = await LoadAsync(mutationLock, cancellationToken).ConfigureAwait(false);
            var existing = catalog.Entries.SingleOrDefault(entry => entry.WakeEvidence.Count > 0
                && string.Equals(entry.WakeEvidence[^1].Identity.WakeId, capturedCurrent!.Identity.WakeId, StringComparison.Ordinal));
            if (existing is null)
            {
                return WakeMutation(GovernedLoopWakeEvidenceMutationStatus.Conflict);
            }

            var currentWake = existing.WakeEvidence[^1];
            var replayIndex = existing.WakeEvidence.Select((item, index) => (item, index))
                .Where(candidate => string.Equals(candidate.item.ContentHash, capturedNext!.ContentHash, StringComparison.Ordinal))
                .Select(candidate => candidate.index)
                .SingleOrDefault(-1);
            if (replayIndex > 0
                && string.Equals(existing.WakeEvidence[replayIndex - 1].ContentHash, capturedCurrent!.ContentHash, StringComparison.Ordinal))
            {
                return WakeMutation(GovernedLoopWakeEvidenceMutationStatus.Replayed, currentWake);
            }

            if (replayIndex >= 0)
            {
                return WakeMutation(GovernedLoopWakeEvidenceMutationStatus.Conflict, currentWake);
            }

            if (!string.Equals(currentWake.ContentHash, capturedCurrent!.ContentHash, StringComparison.Ordinal)
                || existing.WakeEvidence.Count >= GovernedLoopSleepStoreCodec.MaximumWakeEvidenceItems)
            {
                return WakeMutation(GovernedLoopWakeEvidenceMutationStatus.Conflict, currentWake);
            }

            var replacement = existing with
            {
                WakeEvidence = Array.AsReadOnly(existing.WakeEvidence.Append(capturedNext!).ToArray()),
            };
            var candidate = Replace(catalog, existing, replacement);
            await WriteAsync(candidate, identity, mutationLock, cancellationToken).ConfigureAwait(false);
            return WakeMutation(GovernedLoopWakeEvidenceMutationStatus.Committed, capturedNext);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (GovernedLoopSleepStoreBoundaryObserverException)
        {
            return WakeMutation(GovernedLoopWakeEvidenceMutationStatus.Ambiguous);
        }
        catch (Exception exception) when (IsConflict(exception))
        {
            return WakeMutation(GovernedLoopWakeEvidenceMutationStatus.Conflict);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return WakeMutation(GovernedLoopWakeEvidenceMutationStatus.Unavailable);
        }
    }

    internal async Task<GovernedLoopBackgroundWorkReadResult> ReadCandidatesAsync(
        GovernedLoopBackgroundWorkFamily family,
        DateTimeOffset observedAtUtc,
        int maximumCandidates,
        string? afterCheckpointId,
        CancellationToken cancellationToken)
    {
        if (family is not (GovernedLoopBackgroundWorkFamily.Wake or GovernedLoopBackgroundWorkFamily.WakeReconciliation)
            || !GovernedLoopBackgroundWorkContract.IsValidReadRequest(observedAtUtc, maximumCandidates)
            || afterCheckpointId is not null && !IsHash(afterCheckpointId))
        {
            return Background(GovernedLoopBackgroundWorkReadStatus.Corrupt);
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var mutationLock = await _guard.AcquireMutationLockAsync(_observer, cancellationToken).ConfigureAwait(false);
            var (catalog, _) = await LoadAsync(mutationLock, cancellationToken).ConfigureAwait(false);
            return family == GovernedLoopBackgroundWorkFamily.Wake
                ? WakePage(
                    catalog.Entries
                        .Where(entry => entry.WakeEvidence.Count == 0
                            && entry.Checkpoint.WakeMode == GovernedLoopWakeMode.Timestamp
                            && entry.Checkpoint.WakeDeadlineUtc <= observedAtUtc)
                        .Select(entry => new GovernedLoopWakeRequest(
                            entry.Checkpoint.CheckpointId,
                            entry.Checkpoint.ContentHash,
                            null))
                        .OrderBy(item => item.CheckpointId, StringComparer.Ordinal)
                        .ToArray(),
                    afterCheckpointId,
                    maximumCandidates)
                : ReconciliationPage(
                    catalog.Entries
                        .Where(entry => entry.WakeEvidence.Count > 0
                            && entry.WakeEvidence[^1].Disposition is GovernedLoopWakeDisposition.Prepared or GovernedLoopWakeDisposition.AmbiguousAttempt)
                        .Select(entry => new GovernedLoopWakeReconciliationRequest(
                            entry.Checkpoint.CheckpointId,
                            entry.WakeEvidence[^1].Identity.WakeId))
                        .OrderBy(item => item.CheckpointId, StringComparer.Ordinal)
                        .ThenBy(item => item.WakeId, StringComparer.Ordinal)
                        .ToArray(),
                    afterCheckpointId,
                    maximumCandidates);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is GovernedLoopSleepStoreLimitException or TriggerQueuePersistenceBackpressureException)
        {
            return Background(GovernedLoopBackgroundWorkReadStatus.Backpressured);
        }
        catch (Exception exception) when (IsConflict(exception))
        {
            return Background(GovernedLoopBackgroundWorkReadStatus.Corrupt);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return Background(GovernedLoopBackgroundWorkReadStatus.Unavailable);
        }
    }

    private static GovernedLoopBackgroundWorkReadResult WakePage(
        IReadOnlyList<GovernedLoopWakeRequest> candidates,
        string? afterCheckpointId,
        int maximumCandidates)
    {
        if (candidates.Count == 0)
        {
            return Background(GovernedLoopBackgroundWorkReadStatus.Empty);
        }

        var page = SelectCandidatePage(candidates, item => item.CheckpointId, afterCheckpointId, maximumCandidates);
        return GovernedLoopBackgroundWorkReadResultFactory.CreateDetached(
            GovernedLoopBackgroundWorkReadStatus.Empty,
            [],
            GovernedLoopBackgroundWorkReadStatus.Found,
            page,
            GovernedLoopBackgroundWorkReadStatus.Empty,
            [],
            wakePageTruncated: candidates.Count > page.Count);
    }

    private static GovernedLoopBackgroundWorkReadResult ReconciliationPage(
        IReadOnlyList<GovernedLoopWakeReconciliationRequest> candidates,
        string? afterCheckpointId,
        int maximumCandidates)
    {
        if (candidates.Count == 0)
        {
            return Background(GovernedLoopBackgroundWorkReadStatus.Empty);
        }

        var page = SelectCandidatePage(candidates, item => item.CheckpointId, afterCheckpointId, maximumCandidates);
        return GovernedLoopBackgroundWorkReadResultFactory.CreateDetached(
            GovernedLoopBackgroundWorkReadStatus.Empty,
            [],
            GovernedLoopBackgroundWorkReadStatus.Empty,
            [],
            GovernedLoopBackgroundWorkReadStatus.Found,
            page,
            wakeReconciliationPageTruncated: candidates.Count > page.Count);
    }

    private static IReadOnlyList<T> SelectCandidatePage<T>(
        IReadOnlyList<T> candidates,
        Func<T, string> key,
        string? afterCheckpointId,
        int maximumCandidates)
    {
        var start = 0;
        if (afterCheckpointId is not null)
        {
            start = candidates.ToList().FindIndex(
                candidate => string.Compare(key(candidate), afterCheckpointId, StringComparison.Ordinal) > 0);
            if (start < 0)
            {
                start = 0;
            }
        }

        var count = Math.Min(maximumCandidates, candidates.Count);
        return Array.AsReadOnly(
            Enumerable.Range(0, count)
                .Select(offset => candidates[(start + offset) % candidates.Count])
                .ToArray());
    }

    private async Task<(GovernedLoopSleepStoreCatalog Catalog, TriggerQueueReadResult Identity)> LoadAsync(
        TriggerQueueMutationLease mutationLock,
        CancellationToken cancellationToken)
    {
        var identity = await _guard.ReadLatestAsync(_maximumCatalogBytes, _observer, mutationLock, cancellationToken).ConfigureAwait(false);
        if (identity.LatestContent is null)
        {
            return (new GovernedLoopSleepStoreCatalog(SchemaVersion, 0, []), identity);
        }

        var catalog = GovernedLoopSleepStoreCodec.Deserialize(identity.LatestContent, _maximumCheckpoints, _maximumCatalogBytes);
        if (identity.Artifacts.Count == 0 || catalog.Generation != identity.Artifacts[^1].Generation)
        {
            throw new FormatException("The sleep ledger generation does not match its immutable artifact name.");
        }

        return (catalog, identity);
    }

    private async Task WriteAsync(
        GovernedLoopSleepStoreCatalog candidate,
        TriggerQueueReadResult identity,
        TriggerQueueMutationLease mutationLock,
        CancellationToken cancellationToken)
    {
        var content = GovernedLoopSleepStoreCodec.Serialize(candidate, _maximumCatalogBytes);
        cancellationToken.ThrowIfCancellationRequested();
        await _guard.WriteAsync(
            content,
            identity.Artifacts,
            identity.Tombstones,
            identity.Precursors,
            candidate.Generation,
            _observer,
            mutationLock).ConfigureAwait(false);
    }

    private static GovernedLoopSleepStoreCatalog Replace(
        GovernedLoopSleepStoreCatalog catalog,
        GovernedLoopSleepStoreEntry current,
        GovernedLoopSleepStoreEntry replacement)
        => new(
            SchemaVersion,
            checked(catalog.Generation + 1),
            Ordered(catalog.Entries.Select(entry => ReferenceEquals(entry, current) ? replacement : entry)));

    private static IReadOnlyList<GovernedLoopSleepStoreEntry> Ordered(IEnumerable<GovernedLoopSleepStoreEntry> entries)
        => Array.AsReadOnly(entries.OrderBy(entry => entry.Checkpoint.CheckpointId, StringComparer.Ordinal).ToArray());

    private static bool SameCheckpointIdentity(
        GovernedLoopSleepCheckpoint proposed,
        GovernedLoopSleepCheckpoint existing)
        => string.Equals(proposed.CheckpointId, existing.CheckpointId, StringComparison.Ordinal)
            && Equals(proposed.Binding, existing.Binding)
            && proposed.WakeMode == existing.WakeMode
            && proposed.WakeDeadlineUtc == existing.WakeDeadlineUtc
            && existing.PublishedAtUtc <= proposed.PublishedAtUtc
            && string.Equals(proposed.AuthenticatedEventReference, existing.AuthenticatedEventReference, StringComparison.Ordinal);

    private static bool TryCaptureCheckpoint(
        GovernedLoopSleepCheckpoint? checkpoint,
        string? expectedPostureHash,
        out GovernedLoopSleepCheckpoint? captured)
    {
        captured = null;
        try
        {
            captured = Copy(checkpoint);
            return IsHash(expectedPostureHash)
                && GovernedLoopSleepContractValidator.Validate(captured).IsValid;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NullReferenceException)
        {
            return false;
        }
    }

    private static bool TryCaptureWake(GovernedLoopWakeEvidence? evidence, out GovernedLoopWakeEvidence? captured)
    {
        captured = null;
        try
        {
            captured = Copy(evidence);
            return GovernedLoopSleepContractValidator.Validate(captured).IsValid;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NullReferenceException)
        {
            return false;
        }
    }

    private static GovernedLoopSleepCheckpoint Copy(GovernedLoopSleepCheckpoint? checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var binding = checkpoint.Binding;
        var execution = binding.Execution;
        var revision = GovernedLoopRevisionReference.Create(
            execution.Revision.SchemaVersion,
            execution.Revision.GraphId,
            execution.Revision.RevisionId,
            execution.Revision.ExecutableHash);
        var executionCopy = GovernedLoopExecutionBinding.Create(execution.SchemaVersion, execution.RunId, revision, execution.ExecutionGeneration);
        var publicationRevision = GovernedLoopRevisionReference.Create(
            binding.Publication.Revision.SchemaVersion,
            binding.Publication.Revision.GraphId,
            binding.Publication.Revision.RevisionId,
            binding.Publication.Revision.ExecutableHash);
        var publicationCopy = new GovernedLoopRevisionPublicationPin(
            binding.Publication.SchemaVersion,
            publicationRevision,
            binding.Publication.PublicationOperationId,
            binding.Publication.ValidationEvidenceHash);
        var bindingCopy = new GovernedLoopSleepBinding(
            executionCopy,
            publicationCopy,
            binding.FrontierVersion,
            binding.FrontierHash,
            binding.ActivationOrdinal,
            binding.CycleId,
            binding.CycleIteration,
            binding.NodeId,
            binding.NodeVisitOrdinal,
            binding.WaitAttempt,
            binding.WaitOperationId);
        return new GovernedLoopSleepCheckpoint(
            checkpoint.SchemaVersion,
            checkpoint.CheckpointId,
            bindingCopy,
            checkpoint.WakeMode,
            checkpoint.WakeDeadlineUtc,
            checkpoint.AuthenticatedEventReference,
            checkpoint.PublishedAtUtc,
            checkpoint.ContentHash);
    }

    private static GovernedLoopWakeEvidence Copy(GovernedLoopWakeEvidence? evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var identity = evidence.Identity;
        var identityCopy = new GovernedLoopWakeIdentity(
            identity.SchemaVersion,
            identity.WakeId,
            identity.CheckpointId,
            identity.CheckpointHash,
            identity.WakeMode,
            identity.AuthenticatedEventReference,
            identity.AuthenticationEvidenceHash,
            identity.ContentHash);
        return new GovernedLoopWakeEvidence(
            evidence.SchemaVersion,
            evidence.EvidenceVersion,
            identityCopy,
            evidence.Disposition,
            evidence.ContinuationOperationId,
            evidence.ContinuationEvidenceHash,
            evidence.DispositionEvidenceReference,
            evidence.RecordedAtUtc,
            evidence.ContentHash);
    }

    private static GovernedLoopSleepCheckpointMutationResult CheckpointMutation(
        GovernedLoopSleepCheckpointMutationStatus status,
        GovernedLoopSleepCheckpoint? checkpoint = null)
        => new(status, checkpoint is null ? null : Copy(checkpoint));

    private static GovernedLoopSleepCheckpointReadResult CheckpointRead(
        GovernedLoopSleepStoreReadStatus status,
        GovernedLoopSleepCheckpoint? checkpoint = null)
        => new(status, checkpoint is null ? null : Copy(checkpoint));

    private static GovernedLoopWakeEvidenceMutationResult WakeMutation(
        GovernedLoopWakeEvidenceMutationStatus status,
        GovernedLoopWakeEvidence? evidence = null)
        => new(status, evidence is null ? null : Copy(evidence));

    private static GovernedLoopWakeEvidenceReadResult WakeRead(
        GovernedLoopSleepStoreReadStatus status,
        GovernedLoopWakeEvidence? evidence = null,
        GovernedLoopWakeEvidence? preparedEvidence = null)
        => new(
            status,
            evidence is null ? null : Copy(evidence),
            preparedEvidence is null ? null : Copy(preparedEvidence));

    private static GovernedLoopWakeCatalogEvidenceReadResult Operational(GovernedLoopOperationalEvidenceReadStatus status)
        => new(status, 0, false, null, Array.AsReadOnly(Array.Empty<GovernedLoopWakeEvidenceSnapshot>()));

    private static GovernedLoopBackgroundWorkReadResult Background(
        GovernedLoopBackgroundWorkReadStatus status,
        IReadOnlyList<GovernedLoopWakeRequest>? wakes = null,
        IReadOnlyList<GovernedLoopWakeReconciliationRequest>? reconciliations = null)
        => GovernedLoopBackgroundWorkReadResultFactory.CreateDetached(status, [], wakes ?? [], reconciliations ?? []);

    private static GovernedLoopBackgroundWorkReadResult Background(
        GovernedLoopBackgroundWorkReadStatus wakeStatus,
        IReadOnlyList<GovernedLoopWakeRequest> wakes,
        GovernedLoopBackgroundWorkReadStatus reconciliationStatus,
        IReadOnlyList<GovernedLoopWakeReconciliationRequest> reconciliations)
        => GovernedLoopBackgroundWorkReadResultFactory.CreateDetached(
            GovernedLoopBackgroundWorkReadStatus.Empty,
            [],
            wakeStatus,
            wakes,
            reconciliationStatus,
            reconciliations);

    private static bool IsHash(string? value)
        => value is { Length: GovernedLoopSleepContractLimits.Sha256HexCharacters }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsConflict(Exception exception)
        => exception is GovernedLoopSleepStoreLimitException
            or TriggerQueuePersistenceBackpressureException
            or FormatException
            or InvalidOperationException
            or OverflowException
            or ArgumentException
            or Win32Exception;

    private static bool IsUnavailable(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or TimeoutException
            or PlatformNotSupportedException
            or NotSupportedException;

    private static void ValidateOptions(GovernedLoopSleepStoreOptions options)
    {
        if (options.MaxCheckpoints is < 1 or > MaximumConfiguredCheckpoints)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The sleep checkpoint count bound is outside the supported range.");
        }

        if (options.MaxCatalogUtf8Bytes is < 1 or > MaximumConfiguredCatalogBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The sleep catalog byte bound is outside the supported range.");
        }

        if (options.MaxDurabilityArtifacts is < 1 or > MaximumConfiguredDurabilityArtifacts)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The sleep durability-artifact bound is outside the supported range.");
        }
    }
}
