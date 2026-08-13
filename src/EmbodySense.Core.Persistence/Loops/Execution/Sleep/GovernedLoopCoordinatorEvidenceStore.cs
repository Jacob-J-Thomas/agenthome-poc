using System.ComponentModel;
using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Persistence.Triggers;
using EmbodySense.Core.Persistence.Triggers.Models;

namespace EmbodySense.Core.Persistence.Loops.Execution.Sleep;

/// <summary>Persists fenced local coordinator ownership and append-only lifecycle, heartbeat, and failure evidence.</summary>
/// <remarks>Every mutation publishes one complete immutable generation under a cross-process lease. Ownership history is never replaced; a successor is appended only after exact prior hashes and exclusive lease expiry are proven.</remarks>
public sealed class GovernedLoopCoordinatorEvidenceStore : IGovernedLoopCoordinatorEvidencePort
{
    private const int SchemaVersion = 1;
    private const int MaximumConfiguredCoordinators = 256;
    private const int MaximumConfiguredEvidenceItems = 65_536;
    private const int MaximumConfiguredCatalogBytes = 128 * 1024 * 1024;
    private const int MaximumConfiguredDurabilityArtifacts = 64;
    private readonly TriggerQueueArtifactGuard _guard;
    private readonly int _maximumCatalogBytes;
    private readonly int _maximumCoordinators;
    private readonly int _maximumEvidenceItems;
    private readonly ITriggerQueueDurabilityObserver _observer;

    /// <summary>Creates one bounded workspace coordinator evidence store.</summary>
    public GovernedLoopCoordinatorEvidenceStore(WorkspacePaths paths, GovernedLoopCoordinatorEvidenceStoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        options ??= new GovernedLoopCoordinatorEvidenceStoreOptions();
        ValidateOptions(options);
        _maximumCatalogBytes = options.MaxCatalogUtf8Bytes;
        _maximumCoordinators = options.MaxCoordinators;
        _maximumEvidenceItems = options.MaxEvidenceItemsPerCoordinator;
        _observer = new GovernedLoopSleepStoreDurabilityObserver(options.DurableBoundaryObserver);
        _guard = new TriggerQueueArtifactGuard(
            paths.RootPath,
            paths.AgentFile(Path.Combine("loops", "execution", "coordinator")),
            options.MaxDurabilityArtifacts);
    }

    /// <inheritdoc />
    public async Task<GovernedLoopCoordinatorReadResult?> ReadAsync(string coordinatorId, CancellationToken cancellationToken = default)
    {
        if (!GovernedLoopCoordinatorEvidenceContract.IsValidCoordinatorId(coordinatorId))
        {
            return ReadResult(GovernedLoopCoordinatorReadStatus.Corrupt);
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var mutationLock = await _guard.AcquireMutationLockAsync(_observer, cancellationToken).ConfigureAwait(false);
            var (catalog, _) = await LoadAsync(mutationLock, cancellationToken).ConfigureAwait(false);
            var entry = catalog.Entries.SingleOrDefault(candidate => string.Equals(candidate.CoordinatorId, coordinatorId, StringComparison.Ordinal));
            return entry is null
                ? ReadResult(GovernedLoopCoordinatorReadStatus.NotFound)
                : ReadResult(GovernedLoopCoordinatorReadStatus.Found, Snapshot(entry));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsCorrupt(exception))
        {
            return ReadResult(GovernedLoopCoordinatorReadStatus.Corrupt);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return ReadResult(GovernedLoopCoordinatorReadStatus.Unavailable);
        }
    }

    /// <inheritdoc />
    public async Task<GovernedLoopCoordinatorAcquisitionResult?> TryAcquireAsync(
        GovernedLoopCoordinatorAcquisitionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!GovernedLoopCoordinatorEvidenceContract.IsValid(request))
        {
            return Acquisition(GovernedLoopCoordinatorAcquisitionStatus.Corrupt);
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var mutationLock = await _guard.AcquireMutationLockAsync(_observer, cancellationToken).ConfigureAwait(false);
            var (catalog, identity) = await LoadAsync(mutationLock, cancellationToken).ConfigureAwait(false);
            var coordinatorId = request.ProposedOwnership.CoordinatorId;
            var entry = catalog.Entries.SingleOrDefault(candidate => string.Equals(candidate.CoordinatorId, coordinatorId, StringComparison.Ordinal));
            if (entry is null)
            {
                if (request.PriorEvidenceExpectation != GovernedLoopCoordinatorPriorEvidenceExpectation.NotFound)
                {
                    return Acquisition(GovernedLoopCoordinatorAcquisitionStatus.Conflict);
                }

                if (catalog.Entries.Count >= _maximumCoordinators || _maximumEvidenceItems < 3)
                {
                    return Acquisition(GovernedLoopCoordinatorAcquisitionStatus.Conflict);
                }

                var created = new GovernedLoopCoordinatorEvidenceStoreEntry(
                    coordinatorId,
                    Array.AsReadOnly([request.ProposedOwnership]),
                    Array.AsReadOnly([request.StartingLifecycle]),
                    Array.AsReadOnly([request.InitialHeartbeat]),
                    Array.AsReadOnly<GovernedLoopCoordinatorFailure>([]));
                var candidate = new GovernedLoopCoordinatorEvidenceStoreCatalog(
                    SchemaVersion,
                    checked(catalog.Generation + 1),
                    Ordered(catalog.Entries.Append(created)));
                await WriteAsync(candidate, identity, mutationLock, cancellationToken).ConfigureAwait(false);
                return Acquisition(GovernedLoopCoordinatorAcquisitionStatus.Acquired, Snapshot(created));
            }

            if (ContainsAcquisition(entry, request))
            {
                return Acquisition(GovernedLoopCoordinatorAcquisitionStatus.Duplicate, Snapshot(entry));
            }

            if (request.PriorEvidenceExpectation == GovernedLoopCoordinatorPriorEvidenceExpectation.NotFound)
            {
                return Acquisition(GovernedLoopCoordinatorAcquisitionStatus.OwnedByLivePeer, Snapshot(entry));
            }

            var currentOwnership = entry.Ownerships[^1];
            var currentHeartbeat = LatestHeartbeat(entry, currentOwnership);
            if (!string.Equals(currentOwnership.ContentHash, request.ExpectedOwnershipHash, StringComparison.Ordinal)
                || !string.Equals(currentHeartbeat.ContentHash, request.ExpectedHeartbeatHash, StringComparison.Ordinal))
            {
                return Acquisition(GovernedLoopCoordinatorAcquisitionStatus.Conflict, Snapshot(entry));
            }

            if (request.ProposedOwnership.AcquiredAtUtc < currentHeartbeat.LeaseExpiresAtUtc)
            {
                return Acquisition(GovernedLoopCoordinatorAcquisitionStatus.LeaseNotExpired, Snapshot(entry));
            }

            if (!GovernedLoopSleepContractValidator.ValidateHandoff(currentOwnership, currentHeartbeat, request.ProposedOwnership).IsValid)
            {
                return Acquisition(GovernedLoopCoordinatorAcquisitionStatus.Corrupt);
            }

            if (EvidenceCount(entry) > _maximumEvidenceItems - 3)
            {
                return Acquisition(GovernedLoopCoordinatorAcquisitionStatus.Conflict, Snapshot(entry));
            }

            var replacement = new GovernedLoopCoordinatorEvidenceStoreEntry(
                entry.CoordinatorId,
                ReadOnly(entry.Ownerships.Append(request.ProposedOwnership)),
                ReadOnly(entry.Lifecycles.Append(request.StartingLifecycle)),
                ReadOnly(entry.Heartbeats.Append(request.InitialHeartbeat)),
                entry.Failures);
            await WriteReplacementAsync(catalog, entry, replacement, identity, mutationLock, cancellationToken).ConfigureAwait(false);
            return Acquisition(GovernedLoopCoordinatorAcquisitionStatus.Acquired, Snapshot(replacement));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsCorrupt(exception))
        {
            return Acquisition(GovernedLoopCoordinatorAcquisitionStatus.Corrupt);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return Acquisition(GovernedLoopCoordinatorAcquisitionStatus.Unavailable);
        }
    }

    /// <inheritdoc />
    public Task<GovernedLoopCoordinatorHeartbeatMutationResult?> RenewHeartbeatAsync(
        GovernedLoopCoordinatorHeartbeatMutationRequest request,
        CancellationToken cancellationToken = default)
        => MutateHeartbeatAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<GovernedLoopCoordinatorLifecycleMutationResult?> AppendLifecycleAsync(
        GovernedLoopCoordinatorLifecycleMutationRequest request,
        CancellationToken cancellationToken = default)
        => MutateLifecycleAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<GovernedLoopCoordinatorFailureMutationResult?> AppendFailureAsync(
        GovernedLoopCoordinatorFailureMutationRequest request,
        CancellationToken cancellationToken = default)
        => MutateFailureAsync(request, cancellationToken);

    private async Task<GovernedLoopCoordinatorHeartbeatMutationResult?> MutateHeartbeatAsync(
        GovernedLoopCoordinatorHeartbeatMutationRequest request,
        CancellationToken cancellationToken)
    {
        if (!GovernedLoopCoordinatorEvidenceContract.IsValid(request))
        {
            return HeartbeatResult(GovernedLoopCoordinatorHeartbeatMutationStatus.Corrupt);
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var mutationLock = await _guard.AcquireMutationLockAsync(_observer, cancellationToken).ConfigureAwait(false);
            var (catalog, identity) = await LoadAsync(mutationLock, cancellationToken).ConfigureAwait(false);
            var entry = Find(catalog, request.ExpectedOwnership.CoordinatorId);
            if (entry is null || !IsCurrentOwner(entry, request.ExpectedOwnershipHash))
            {
                return HeartbeatResult(GovernedLoopCoordinatorHeartbeatMutationStatus.OwnershipLost, entry is null ? null : Snapshot(entry));
            }

            var latest = LatestHeartbeat(entry, entry.Ownerships[^1]);
            if (string.Equals(latest.ContentHash, request.ProposedHeartbeat.ContentHash, StringComparison.Ordinal))
            {
                return HeartbeatResult(GovernedLoopCoordinatorHeartbeatMutationStatus.Duplicate, Snapshot(entry));
            }

            if (latest.HeartbeatSequence != request.ExpectedHeartbeatSequence
                || !string.Equals(latest.ContentHash, request.ExpectedHeartbeatHash, StringComparison.Ordinal))
            {
                return HeartbeatResult(GovernedLoopCoordinatorHeartbeatMutationStatus.Conflict, Snapshot(entry));
            }

            if (!GovernedLoopSleepContractValidator.ValidateTransition(latest, request.ProposedHeartbeat).IsValid)
            {
                return HeartbeatResult(GovernedLoopCoordinatorHeartbeatMutationStatus.Corrupt);
            }

            if (EvidenceCount(entry) >= _maximumEvidenceItems)
            {
                return HeartbeatResult(GovernedLoopCoordinatorHeartbeatMutationStatus.Conflict, Snapshot(entry));
            }

            var replacement = new GovernedLoopCoordinatorEvidenceStoreEntry(
                entry.CoordinatorId,
                entry.Ownerships,
                entry.Lifecycles,
                ReadOnly(entry.Heartbeats.Append(request.ProposedHeartbeat)),
                entry.Failures);
            await WriteReplacementAsync(catalog, entry, replacement, identity, mutationLock, cancellationToken).ConfigureAwait(false);
            return HeartbeatResult(GovernedLoopCoordinatorHeartbeatMutationStatus.Renewed, Snapshot(replacement));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsCorrupt(exception))
        {
            return HeartbeatResult(GovernedLoopCoordinatorHeartbeatMutationStatus.Corrupt);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return HeartbeatResult(GovernedLoopCoordinatorHeartbeatMutationStatus.Unavailable);
        }
    }

    private async Task<GovernedLoopCoordinatorLifecycleMutationResult?> MutateLifecycleAsync(
        GovernedLoopCoordinatorLifecycleMutationRequest request,
        CancellationToken cancellationToken)
    {
        if (!GovernedLoopCoordinatorEvidenceContract.IsValid(request))
        {
            return LifecycleResult(GovernedLoopCoordinatorLifecycleMutationStatus.Corrupt);
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var mutationLock = await _guard.AcquireMutationLockAsync(_observer, cancellationToken).ConfigureAwait(false);
            var (catalog, identity) = await LoadAsync(mutationLock, cancellationToken).ConfigureAwait(false);
            var entry = Find(catalog, request.ExpectedOwnership.CoordinatorId);
            if (entry is null || !IsCurrentOwner(entry, request.ExpectedOwnershipHash))
            {
                return LifecycleResult(GovernedLoopCoordinatorLifecycleMutationStatus.OwnershipLost, entry is null ? null : Snapshot(entry));
            }

            var latest = LatestLifecycle(entry, entry.Ownerships[^1]);
            if (string.Equals(latest.ContentHash, request.ProposedLifecycle.ContentHash, StringComparison.Ordinal))
            {
                return LifecycleResult(GovernedLoopCoordinatorLifecycleMutationStatus.Duplicate, Snapshot(entry));
            }

            if (latest.LifecycleVersion != request.ExpectedLifecycleVersion
                || !string.Equals(latest.ContentHash, request.ExpectedLifecycleHash, StringComparison.Ordinal))
            {
                return LifecycleResult(GovernedLoopCoordinatorLifecycleMutationStatus.Conflict, Snapshot(entry));
            }

            if (!GovernedLoopSleepContractValidator.ValidateTransition(latest, request.ProposedLifecycle).IsValid)
            {
                return LifecycleResult(GovernedLoopCoordinatorLifecycleMutationStatus.Corrupt);
            }

            if (EvidenceCount(entry) >= _maximumEvidenceItems)
            {
                return LifecycleResult(GovernedLoopCoordinatorLifecycleMutationStatus.Conflict, Snapshot(entry));
            }

            var replacement = new GovernedLoopCoordinatorEvidenceStoreEntry(
                entry.CoordinatorId,
                entry.Ownerships,
                ReadOnly(entry.Lifecycles.Append(request.ProposedLifecycle)),
                entry.Heartbeats,
                entry.Failures);
            await WriteReplacementAsync(catalog, entry, replacement, identity, mutationLock, cancellationToken).ConfigureAwait(false);
            return LifecycleResult(GovernedLoopCoordinatorLifecycleMutationStatus.Appended, Snapshot(replacement));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsCorrupt(exception))
        {
            return LifecycleResult(GovernedLoopCoordinatorLifecycleMutationStatus.Corrupt);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return LifecycleResult(GovernedLoopCoordinatorLifecycleMutationStatus.Unavailable);
        }
    }

    private async Task<GovernedLoopCoordinatorFailureMutationResult?> MutateFailureAsync(
        GovernedLoopCoordinatorFailureMutationRequest request,
        CancellationToken cancellationToken)
    {
        if (!GovernedLoopCoordinatorEvidenceContract.IsValid(request))
        {
            return FailureResult(GovernedLoopCoordinatorFailureMutationStatus.Corrupt);
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var mutationLock = await _guard.AcquireMutationLockAsync(_observer, cancellationToken).ConfigureAwait(false);
            var (catalog, identity) = await LoadAsync(mutationLock, cancellationToken).ConfigureAwait(false);
            var entry = Find(catalog, request.ExpectedOwnership.CoordinatorId);
            if (entry is null || !IsCurrentOwner(entry, request.ExpectedOwnershipHash))
            {
                return FailureResult(GovernedLoopCoordinatorFailureMutationStatus.OwnershipLost, entry is null ? null : Snapshot(entry));
            }

            var currentOwnership = entry.Ownerships[^1];
            var latest = LatestFailure(entry, currentOwnership);
            if (latest is not null && string.Equals(latest.ContentHash, request.ProposedFailure.ContentHash, StringComparison.Ordinal))
            {
                return FailureResult(GovernedLoopCoordinatorFailureMutationStatus.Duplicate, Snapshot(entry));
            }

            var matches = request.PriorFailureExpectation switch
            {
                GovernedLoopCoordinatorPriorFailureExpectation.None => latest is null,
                GovernedLoopCoordinatorPriorFailureExpectation.Existing => latest is not null
                    && latest.FailureSequence == request.ExpectedFailureSequence
                    && string.Equals(latest.ContentHash, request.ExpectedFailureHash, StringComparison.Ordinal),
                _ => false
            };
            if (!matches)
            {
                return FailureResult(GovernedLoopCoordinatorFailureMutationStatus.Conflict, Snapshot(entry));
            }

            if (latest is not null && !GovernedLoopSleepContractValidator.ValidateTransition(latest, request.ProposedFailure).IsValid)
            {
                return FailureResult(GovernedLoopCoordinatorFailureMutationStatus.Corrupt);
            }

            if (EvidenceCount(entry) >= _maximumEvidenceItems)
            {
                return FailureResult(GovernedLoopCoordinatorFailureMutationStatus.Conflict, Snapshot(entry));
            }

            var replacement = new GovernedLoopCoordinatorEvidenceStoreEntry(
                entry.CoordinatorId,
                entry.Ownerships,
                entry.Lifecycles,
                entry.Heartbeats,
                ReadOnly(entry.Failures.Append(request.ProposedFailure)));
            await WriteReplacementAsync(catalog, entry, replacement, identity, mutationLock, cancellationToken).ConfigureAwait(false);
            return FailureResult(GovernedLoopCoordinatorFailureMutationStatus.Appended, Snapshot(replacement));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsCorrupt(exception))
        {
            return FailureResult(GovernedLoopCoordinatorFailureMutationStatus.Corrupt);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return FailureResult(GovernedLoopCoordinatorFailureMutationStatus.Unavailable);
        }
    }

    private async Task<(GovernedLoopCoordinatorEvidenceStoreCatalog Catalog, TriggerQueueReadResult Identity)> LoadAsync(
        TriggerQueueMutationLease mutationLock,
        CancellationToken cancellationToken)
    {
        var identity = await _guard.ReadLatestAsync(_maximumCatalogBytes, _observer, mutationLock, cancellationToken).ConfigureAwait(false);
        if (identity.LatestContent is null)
        {
            return (new GovernedLoopCoordinatorEvidenceStoreCatalog(SchemaVersion, 0, []), identity);
        }

        var catalog = GovernedLoopCoordinatorEvidenceStoreCodec.Deserialize(
            identity.LatestContent,
            _maximumCoordinators,
            _maximumEvidenceItems,
            _maximumCatalogBytes);
        if (identity.Artifacts.Count == 0 || catalog.Generation != identity.Artifacts[^1].Generation)
        {
            throw new FormatException("The coordinator ledger generation does not match its immutable artifact name.");
        }

        return (catalog, identity);
    }

    private async Task WriteReplacementAsync(
        GovernedLoopCoordinatorEvidenceStoreCatalog catalog,
        GovernedLoopCoordinatorEvidenceStoreEntry current,
        GovernedLoopCoordinatorEvidenceStoreEntry replacement,
        TriggerQueueReadResult identity,
        TriggerQueueMutationLease mutationLock,
        CancellationToken cancellationToken)
    {
        var candidate = new GovernedLoopCoordinatorEvidenceStoreCatalog(
            SchemaVersion,
            checked(catalog.Generation + 1),
            Ordered(catalog.Entries.Select(entry => ReferenceEquals(entry, current) ? replacement : entry)));
        await WriteAsync(candidate, identity, mutationLock, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteAsync(
        GovernedLoopCoordinatorEvidenceStoreCatalog candidate,
        TriggerQueueReadResult identity,
        TriggerQueueMutationLease mutationLock,
        CancellationToken cancellationToken)
    {
        var content = GovernedLoopCoordinatorEvidenceStoreCodec.Serialize(candidate, _maximumEvidenceItems, _maximumCatalogBytes);
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

    private static GovernedLoopCoordinatorEvidenceStoreEntry? Find(
        GovernedLoopCoordinatorEvidenceStoreCatalog catalog,
        string coordinatorId)
        => catalog.Entries.SingleOrDefault(entry => string.Equals(entry.CoordinatorId, coordinatorId, StringComparison.Ordinal));

    private static IReadOnlyList<T> ReadOnly<T>(IEnumerable<T> items)
        => Array.AsReadOnly(items.ToArray());

    private static IReadOnlyList<GovernedLoopCoordinatorEvidenceStoreEntry> Ordered(
        IEnumerable<GovernedLoopCoordinatorEvidenceStoreEntry> entries)
        => Array.AsReadOnly(entries.OrderBy(entry => entry.CoordinatorId, StringComparer.Ordinal).ToArray());

    private static bool ContainsAcquisition(
        GovernedLoopCoordinatorEvidenceStoreEntry entry,
        GovernedLoopCoordinatorAcquisitionRequest request)
        => entry.Ownerships.Any(item => string.Equals(item.ContentHash, request.ProposedOwnership.ContentHash, StringComparison.Ordinal))
            && entry.Lifecycles.Any(item => string.Equals(item.ContentHash, request.StartingLifecycle.ContentHash, StringComparison.Ordinal))
            && entry.Heartbeats.Any(item => string.Equals(item.ContentHash, request.InitialHeartbeat.ContentHash, StringComparison.Ordinal));

    private static bool IsCurrentOwner(GovernedLoopCoordinatorEvidenceStoreEntry entry, string expectedOwnershipHash)
        => string.Equals(entry.Ownerships[^1].ContentHash, expectedOwnershipHash, StringComparison.Ordinal);

    private static GovernedLoopCoordinatorLifecycle LatestLifecycle(
        GovernedLoopCoordinatorEvidenceStoreEntry entry,
        GovernedLoopCoordinatorOwnership ownership)
        => entry.Lifecycles.Last(item => SameOwnership(item.Ownership, ownership));

    private static GovernedLoopCoordinatorHeartbeat LatestHeartbeat(
        GovernedLoopCoordinatorEvidenceStoreEntry entry,
        GovernedLoopCoordinatorOwnership ownership)
        => entry.Heartbeats.Last(item => SameOwnership(item.Ownership, ownership));

    private static GovernedLoopCoordinatorFailure? LatestFailure(
        GovernedLoopCoordinatorEvidenceStoreEntry entry,
        GovernedLoopCoordinatorOwnership ownership)
        => entry.Failures.LastOrDefault(item => SameOwnership(item.Ownership, ownership));

    private static bool SameOwnership(GovernedLoopCoordinatorOwnership first, GovernedLoopCoordinatorOwnership second)
        => string.Equals(first.ContentHash, second.ContentHash, StringComparison.Ordinal);

    private static int EvidenceCount(GovernedLoopCoordinatorEvidenceStoreEntry entry)
        => checked(entry.Ownerships.Count + entry.Lifecycles.Count + entry.Heartbeats.Count + entry.Failures.Count);

    private static GovernedLoopCoordinatorSnapshot Snapshot(GovernedLoopCoordinatorEvidenceStoreEntry entry)
    {
        var ownership = entry.Ownerships[^1];
        var failure = LatestFailure(entry, ownership);
        return new GovernedLoopCoordinatorSnapshot(
            ownership,
            LatestLifecycle(entry, ownership),
            LatestHeartbeat(entry, ownership),
            failure?.FailureSequence ?? 0,
            failure?.ContentHash);
    }

    private static GovernedLoopCoordinatorReadResult ReadResult(
        GovernedLoopCoordinatorReadStatus status,
        GovernedLoopCoordinatorSnapshot? snapshot = null)
        => new(status, snapshot);

    private static GovernedLoopCoordinatorAcquisitionResult Acquisition(
        GovernedLoopCoordinatorAcquisitionStatus status,
        GovernedLoopCoordinatorSnapshot? snapshot = null)
        => new(status, snapshot);

    private static GovernedLoopCoordinatorHeartbeatMutationResult HeartbeatResult(
        GovernedLoopCoordinatorHeartbeatMutationStatus status,
        GovernedLoopCoordinatorSnapshot? snapshot = null)
        => new(status, snapshot);

    private static GovernedLoopCoordinatorLifecycleMutationResult LifecycleResult(
        GovernedLoopCoordinatorLifecycleMutationStatus status,
        GovernedLoopCoordinatorSnapshot? snapshot = null)
        => new(status, snapshot);

    private static GovernedLoopCoordinatorFailureMutationResult FailureResult(
        GovernedLoopCoordinatorFailureMutationStatus status,
        GovernedLoopCoordinatorSnapshot? snapshot = null)
        => new(status, snapshot);

    private static bool IsCorrupt(Exception exception)
        => exception is GovernedLoopSleepStoreLimitException
            or TriggerQueuePersistenceBackpressureException
            or FormatException
            or InvalidOperationException
            or OverflowException
            or ArgumentException
            or Win32Exception;

    private static bool IsUnavailable(Exception exception)
        => exception is GovernedLoopSleepStoreBoundaryObserverException
            or IOException
            or UnauthorizedAccessException
            or TimeoutException
            or PlatformNotSupportedException
            or NotSupportedException;

    private static void ValidateOptions(GovernedLoopCoordinatorEvidenceStoreOptions options)
    {
        if (options.MaxCoordinators is < 1 or > MaximumConfiguredCoordinators)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The coordinator count bound is outside the supported range.");
        }

        if (options.MaxEvidenceItemsPerCoordinator is < 3 or > MaximumConfiguredEvidenceItems)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The coordinator evidence bound is outside the supported range.");
        }

        if (options.MaxCatalogUtf8Bytes is < 1 or > MaximumConfiguredCatalogBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The coordinator catalog byte bound is outside the supported range.");
        }

        if (options.MaxDurabilityArtifacts is < 1 or > MaximumConfiguredDurabilityArtifacts)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The coordinator durability-artifact bound is outside the supported range.");
        }
    }
}
