using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Credentials.Leases;
using EmbodySense.Core.Application.Credentials.Leases.Models;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Leases;
using EmbodySense.Core.Common.Credentials.Leases.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Credentials.Leases.Models;
using EmbodySense.Core.Persistence.Loops;

namespace EmbodySense.Core.Persistence.Credentials.Leases;

/// <summary>Persists bounded, canonical, append-only credential lease histories under hashed storage identities.</summary>
public sealed class CredentialLeaseAttemptStore : ICredentialLeaseAttemptStore
{
    private const int MaximumConfiguredAttempts = 16_384;
    private const long MaximumConfiguredStoreBytes = 512L * 1024 * 1024;
    private const int RequiredProtocolVersions = 4;
    private const int HeadBytes = 71;
    private static readonly TimeSpan _ownerTakeoverTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan _ownerTakeoverPollInterval = TimeSpan.FromMilliseconds(10);
    private readonly CustomLoopArtifactPathGuard _guard;
    private readonly Guid _instanceId = Guid.NewGuid();
    private readonly int _maximumAttempts;
    private readonly int _maximumRecordBytes;
    private readonly long _maximumStoreBytes;
    private readonly int _maximumVersionsPerAttempt;
    private readonly Func<ValueTask>? _ownerTakeoverPollingObserver;
    private readonly string _root;

    /// <summary>Creates one bounded workspace-scoped credential lease-attempt store.</summary>
    public CredentialLeaseAttemptStore(WorkspacePaths paths, CredentialLeaseAttemptStoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        options ??= new CredentialLeaseAttemptStoreOptions();
        if (options.MaxAttempts is < 1 or > MaximumConfiguredAttempts
            || options.MaxRecordUtf8Bytes is < 1 or > CredentialLeaseContractLimits.MaximumRecordUtf8Bytes
            || options.MaxStoreUtf8Bytes is < 1 or > MaximumConfiguredStoreBytes
            || options.MaxStoreUtf8Bytes < checked((long)options.MaxRecordUtf8Bytes * RequiredProtocolVersions + HeadBytes)
            || options.MaxVersionsPerAttempt is < RequiredProtocolVersions or > CredentialLeaseContractLimits.MaximumVersions)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        _maximumAttempts = options.MaxAttempts;
        _maximumRecordBytes = options.MaxRecordUtf8Bytes;
        _maximumStoreBytes = options.MaxStoreUtf8Bytes;
        _maximumVersionsPerAttempt = options.MaxVersionsPerAttempt;
        _ownerTakeoverPollingObserver = options.OwnerTakeoverPollingObserver;
        _root = paths.CredentialLeaseAttemptsPath;
        _guard = new CustomLoopArtifactPathGuard(paths.RootPath);
    }

    /// <inheritdoc />
    public async Task<CredentialLeaseAttemptStoreResult> BeginAsync(CredentialLeaseIntent intent, CredentialLeaseAttemptVersion prepared, CancellationToken cancellationToken = default)
    {
        if (!TryCapture(new CredentialLeaseAttemptHistory(CredentialLeaseAttemptHistory.CurrentSchemaVersion, intent, [prepared]), out var captured)
            || captured.Versions.Count != 1
            || captured.Current.Phase != CredentialLeasePhase.IntentPrepared)
        {
            return Result(CredentialLeaseAttemptStoreStatus.Corrupt);
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var mutation = await AcquireMutationLockAsync(cancellationToken).ConfigureAwait(false);
            ValidateDirectory(out var identities, out var retainedBytes);
            var reservedBytes = await CalculateReservedBytesAsync(identities, cancellationToken).ConfigureAwait(false);
            var storageKey = StorageKey(intent.CredentialUseOperationId, intent.CredentialUseGeneration);
            var current = await ReadCurrentAsync(storageKey, intent.CredentialUseOperationId, intent.CredentialUseGeneration, cancellationToken).ConfigureAwait(false);
            if (current is not null)
            {
                if (!string.Equals(current.Intent.ContentHash, intent.ContentHash, StringComparison.Ordinal))
                {
                    return Result(CredentialLeaseAttemptStoreStatus.Conflict, current);
                }
                if (IsTerminal(current.Current.Phase))
                {
                    return Result(CredentialLeaseAttemptStoreStatus.Replayed, current);
                }

                var replayLease = TryAcquireOwner(intent.CredentialUseOperationId, intent.CredentialUseGeneration, cancellationToken);
                return replayLease is null
                    ? Result(CredentialLeaseAttemptStoreStatus.OperationInProgress, current)
                    : Result(CredentialLeaseAttemptStoreStatus.Replayed, current, replayLease);
            }

            var encoded = CredentialLeaseAttemptRecordCodec.Encode(captured);
            var reusesOwnerOnlyReservation = identities.Contains(storageKey);
            var additionalReservation = reusesOwnerOnlyReservation
                ? 0
                : checked(encoded.Length + HeadBytes + ((long)(RequiredProtocolVersions - 1) * _maximumRecordBytes));
            if (!reusesOwnerOnlyReservation && identities.Count >= _maximumAttempts
                || encoded.Length > _maximumRecordBytes
                || retainedBytes > _maximumStoreBytes - reservedBytes - additionalReservation)
            {
                return Result(CredentialLeaseAttemptStoreStatus.Backpressured);
            }

            var lease = TryAcquireOwner(intent.CredentialUseOperationId, intent.CredentialUseGeneration, cancellationToken);
            if (lease is null)
            {
                return Result(CredentialLeaseAttemptStoreStatus.OperationInProgress);
            }
            try
            {
                await WriteImmutableHistoryAsync(captured, encoded, cancellationToken).ConfigureAwait(false);
                await WriteHeadAsync(captured, cancellationToken).ConfigureAwait(false);
                return Result(CredentialLeaseAttemptStoreStatus.Created, captured, lease);
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsCorrupt(exception))
        {
            return Result(CredentialLeaseAttemptStoreStatus.Corrupt);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return Result(CredentialLeaseAttemptStoreStatus.Unavailable);
        }
    }

    /// <inheritdoc />
    public async Task<CredentialLeaseAttemptStoreResult> ResumeAsync(string credentialUseOperationId, long credentialUseGeneration, CancellationToken cancellationToken = default)
    {
        if (!IsId(credentialUseOperationId) || credentialUseGeneration < 1)
        {
            return Result(CredentialLeaseAttemptStoreStatus.Corrupt);
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var storageKey = StorageKey(credentialUseOperationId, credentialUseGeneration);
            CredentialLeaseAttemptHistory current;
            using (var mutation = await AcquireMutationLockAsync(cancellationToken).ConfigureAwait(false))
            {
                ValidateDirectory(out _, out _);
                var loadedCurrent = await ReadCurrentAsync(storageKey, credentialUseOperationId, credentialUseGeneration, cancellationToken).ConfigureAwait(false);
                if (loadedCurrent is null)
                {
                    return Result(CredentialLeaseAttemptStoreStatus.NotFound);
                }
                current = loadedCurrent;
                if (IsTerminal(current.Current.Phase))
                {
                    return Result(CredentialLeaseAttemptStoreStatus.Replayed, current);
                }

                var immediateLease = TryAcquireOwner(credentialUseOperationId, credentialUseGeneration, cancellationToken);
                if (immediateLease is not null)
                {
                    try
                    {
                        return Result(CredentialLeaseAttemptStoreStatus.Replayed, current, immediateLease);
                    }
                    catch
                    {
                        immediateLease.Dispose();
                        throw;
                    }
                }
            }

            var recoveredLease = await TryAcquireOwnerAsync(credentialUseOperationId, credentialUseGeneration, cancellationToken).ConfigureAwait(false);
            if (recoveredLease is null)
            {
                return Result(CredentialLeaseAttemptStoreStatus.OperationInProgress, current);
            }

            try
            {
                using var mutation = await AcquireMutationLockAsync(cancellationToken).ConfigureAwait(false);
                ValidateDirectory(out _, out _);
                var revalidated = await ReadCurrentAsync(storageKey, credentialUseOperationId, credentialUseGeneration, cancellationToken).ConfigureAwait(false);
                if (revalidated is null)
                {
                    return Result(CredentialLeaseAttemptStoreStatus.NotFound);
                }
                if (IsTerminal(revalidated.Current.Phase))
                {
                    return Result(CredentialLeaseAttemptStoreStatus.Replayed, revalidated);
                }

                cancellationToken.ThrowIfCancellationRequested();
                var result = Result(CredentialLeaseAttemptStoreStatus.Replayed, revalidated, recoveredLease);
                recoveredLease = null;
                return result;
            }
            finally
            {
                recoveredLease?.Dispose();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsCorrupt(exception))
        {
            return Result(CredentialLeaseAttemptStoreStatus.Corrupt);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return Result(CredentialLeaseAttemptStoreStatus.Unavailable);
        }
    }

    /// <inheritdoc />
    public async Task<CredentialLeaseAttemptStoreResult> ReadAsync(string credentialUseOperationId, long credentialUseGeneration, CancellationToken cancellationToken = default)
    {
        if (!IsId(credentialUseOperationId) || credentialUseGeneration < 1)
        {
            return Result(CredentialLeaseAttemptStoreStatus.Corrupt);
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var mutation = await AcquireMutationLockAsync(cancellationToken).ConfigureAwait(false);
            ValidateDirectory(out _, out _);
            var storageKey = StorageKey(credentialUseOperationId, credentialUseGeneration);
            var current = await ReadCurrentAsync(storageKey, credentialUseOperationId, credentialUseGeneration, cancellationToken).ConfigureAwait(false);
            return current is null
                ? Result(CredentialLeaseAttemptStoreStatus.NotFound)
                : Result(CredentialLeaseAttemptStoreStatus.Replayed, current);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsCorrupt(exception))
        {
            return Result(CredentialLeaseAttemptStoreStatus.Corrupt);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return Result(CredentialLeaseAttemptStoreStatus.Unavailable);
        }
    }

    /// <inheritdoc />
    public async Task<CredentialLeaseAttemptStoreResult> CompareExchangeAsync(string expectedContentHash, CredentialLeaseAttemptHistory replacement, ICredentialLeaseAttemptLease lease, CancellationToken cancellationToken = default)
    {
        if (!IsPrefixedHash(expectedContentHash) || !TryCapture(replacement, out var captured))
        {
            return Result(CredentialLeaseAttemptStoreStatus.Corrupt);
        }
        if (lease is not CredentialLeaseAttemptLease owner
            || !owner.Owns(_instanceId, captured.Intent.CredentialUseOperationId, captured.Intent.CredentialUseGeneration))
        {
            return Result(CredentialLeaseAttemptStoreStatus.Conflict);
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var mutation = await AcquireMutationLockAsync(cancellationToken).ConfigureAwait(false);
            ValidateDirectory(out var identities, out var retainedBytes);
            var reservedBytes = await CalculateReservedBytesAsync(identities, cancellationToken).ConfigureAwait(false);
            var storageKey = StorageKey(captured.Intent.CredentialUseOperationId, captured.Intent.CredentialUseGeneration);
            var current = await ReadCurrentAsync(storageKey, captured.Intent.CredentialUseOperationId, captured.Intent.CredentialUseGeneration, cancellationToken).ConfigureAwait(false);
            if (current is null || !string.Equals(current.Current.ContentHash, expectedContentHash, StringComparison.Ordinal))
            {
                return Result(CredentialLeaseAttemptStoreStatus.Conflict, current);
            }
            if (string.Equals(current.Current.ContentHash, captured.Current.ContentHash, StringComparison.Ordinal))
            {
                return HistoriesEqual(current, captured)
                    ? Result(CredentialLeaseAttemptStoreStatus.Replayed, current)
                    : Result(CredentialLeaseAttemptStoreStatus.Conflict, current);
            }
            if (!IsDirectHistorySuccessor(current, captured))
            {
                return Result(CredentialLeaseAttemptStoreStatus.Conflict, current);
            }

            var encoded = CredentialLeaseAttemptRecordCodec.Encode(captured);
            var versionPath = VersionPath(storageKey, captured.Current.ContentHash);
            var versionExists = File.Exists(versionPath);
            var currentReservation = ReservedBytes(current);
            var replacementReservation = ReservedBytes(captured);
            if (!versionExists && CurrentVersionPaths(storageKey).Count >= _maximumVersionsPerAttempt
                || encoded.Length > _maximumRecordBytes
                || !versionExists && retainedBytes > _maximumStoreBytes - encoded.Length - (reservedBytes - currentReservation + replacementReservation))
            {
                return Result(CredentialLeaseAttemptStoreStatus.Backpressured, current);
            }

            await WriteImmutableHistoryAsync(captured, encoded, cancellationToken).ConfigureAwait(false);
            await WriteHeadAsync(captured, cancellationToken).ConfigureAwait(false);
            return Result(CredentialLeaseAttemptStoreStatus.Created, captured);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsCorrupt(exception))
        {
            return Result(CredentialLeaseAttemptStoreStatus.Corrupt);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return Result(CredentialLeaseAttemptStoreStatus.Unavailable);
        }
    }

    private async Task<CredentialLeaseAttemptLease?> TryAcquireOwnerAsync(string operationId, long generation, CancellationToken cancellationToken)
    {
        // Process termination and handle release are separate observations on Windows. Retry only the exact owner
        // marker for a short bounded interval; never delete or bypass retained ownership evidence.
        using var takeoverDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        takeoverDeadline.CancelAfter(_ownerTakeoverTimeout);
        var pollingStarted = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (takeoverDeadline.IsCancellationRequested)
            {
                return null;
            }

            var lease = TryAcquireOwner(operationId, generation, cancellationToken);
            if (lease is not null)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    lease.Dispose();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                if (takeoverDeadline.IsCancellationRequested)
                {
                    lease.Dispose();
                    return null;
                }
                return lease;
            }

            if (!pollingStarted)
            {
                pollingStarted = true;
                QueueOwnerTakeoverPollingObserver();
            }

            try
            {
                await Task.Delay(_ownerTakeoverPollInterval, takeoverDeadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }
    }

    private void QueueOwnerTakeoverPollingObserver()
    {
        var observer = _ownerTakeoverPollingObserver;
        if (observer is null)
        {
            return;
        }

        try
        {
            // Track deterministic blocking-observer deadline coverage under https://github.com/Jacob-J-Thomas/agenthome-poc/issues/515.
            _ = Task.Run(async () =>
            {
                try
                {
                    await observer().ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            });
        }
        catch (Exception)
        {
        }
    }

    private CredentialLeaseAttemptLease? TryAcquireOwner(string operationId, long generation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = _guard.GetFilePath(_root, OwnerFileName(StorageKey(operationId, generation)));
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.WriteThrough);
            if (stream.Length != 0)
            {
                stream.Dispose();
                throw new FormatException("Credential lease ownership evidence must remain value-free.");
            }
            var lease = new CredentialLeaseAttemptLease(_instanceId, operationId, generation, stream);
            if (cancellationToken.IsCancellationRequested)
            {
                lease.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return lease;
        }
        catch (Exception exception) when (IsOwnerMarkerContention(exception))
        {
            return null;
        }
    }

    private static bool IsOwnerMarkerContention(Exception exception)
    {
        const int ResourceTemporarilyUnavailable = 11;
        const int SharingViolation = 32;
        const int LockViolation = 33;
        const int ResourceDeadlockAvoided = 35;
        if (exception is not IOException and not UnauthorizedAccessException)
        {
            return false;
        }

        var errorCode = exception.HResult & 0xFFFF;
        return OperatingSystem.IsWindows()
            ? errorCode is SharingViolation or LockViolation
            : errorCode is ResourceTemporarilyUnavailable or ResourceDeadlockAvoided;
    }

    private async Task<FileStream> AcquireMutationLockAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await _guard.AcquireExclusiveReadLockAsync(_root, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException exception) when (exception.InnerException is IOException && attempt < 4)
            {
                await Task.Yield();
            }
        }
    }

    private void ValidateDirectory(out HashSet<string> identities, out long retainedBytes)
    {
        _guard.PrepareRoot(_root);
        var maximumArtifacts = checked(MaximumConfiguredAttempts * (CredentialLeaseContractLimits.MaximumVersions + 2) + 2);
        var entries = Directory.EnumerateFileSystemEntries(_root).Take(maximumArtifacts + 1).ToArray();
        if (entries.Length > maximumArtifacts || entries.Any(Directory.Exists))
        {
            throw new FormatException("Credential lease storage exceeds its finite artifact bounds.");
        }

        identities = new HashSet<string>(StringComparer.Ordinal);
        var versionIdentities = new HashSet<string>(StringComparer.Ordinal);
        var headIdentities = new HashSet<string>(StringComparer.Ordinal);
        var versionCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        retainedBytes = 0;
        foreach (var entry in entries)
        {
            var fileName = Path.GetFileName(entry);
            if (fileName == ".custom-loop-mutations.lock")
            {
                continue;
            }
            if (IsInterruptedAtomicWrite(fileName))
            {
                throw new FormatException("Credential lease storage contains an interrupted atomic publication requiring inspection.");
            }
            if (TryParseVersionFile(fileName, out var versionStorageKey, out _))
            {
                identities.Add(versionStorageKey);
                versionIdentities.Add(versionStorageKey);
                versionCounts[versionStorageKey] = checked(versionCounts.GetValueOrDefault(versionStorageKey) + 1);
                if (versionCounts[versionStorageKey] > _maximumVersionsPerAttempt)
                {
                    throw new FormatException("Credential lease storage contains too many immutable versions.");
                }
                retainedBytes = checked(retainedBytes + _guard.GetFileLength(_root, entry));
                continue;
            }
            if (TryParseHeadFile(fileName, out var headStorageKey))
            {
                identities.Add(headStorageKey);
                headIdentities.Add(headStorageKey);
                retainedBytes = checked(retainedBytes + _guard.GetFileLength(_root, entry));
                continue;
            }
            if (TryParseOwnerFile(fileName, out var ownerStorageKey))
            {
                if (_guard.GetFileLength(_root, entry) != 0)
                {
                    throw new FormatException("Credential lease owner evidence is malformed.");
                }
                identities.Add(ownerStorageKey);
                continue;
            }

            throw new FormatException("Credential lease storage contains an unsupported artifact.");
        }
        if (!headIdentities.IsSubsetOf(versionIdentities))
        {
            throw new FormatException("Credential lease storage contains a head without immutable evidence.");
        }
    }

    private async Task<CredentialLeaseAttemptHistory?> ReadCurrentAsync(string storageKey, string operationId, long generation, CancellationToken cancellationToken)
    {
        var paths = CurrentVersionPaths(storageKey);
        if (paths.Count == 0)
        {
            return null;
        }

        var byCount = new Dictionary<int, CredentialLeaseAttemptHistory>();
        foreach (var path in paths)
        {
            var history = await ReadHistoryAsync(path, storageKey, operationId, generation, cancellationToken).ConfigureAwait(false);
            if (!byCount.TryAdd(history.Versions.Count, history))
            {
                throw new FormatException("Credential lease evidence contains a forked immutable history.");
            }
        }
        if (byCount.Count > _maximumVersionsPerAttempt || !byCount.ContainsKey(1) || byCount.Keys.Max() != byCount.Count)
        {
            throw new FormatException("Credential lease evidence is missing an immutable history predecessor.");
        }
        for (var count = 2; count <= byCount.Count; count++)
        {
            if (!IsDirectHistorySuccessor(byCount[count - 1], byCount[count]))
            {
                throw new FormatException("Credential lease evidence is disconnected or forked.");
            }
        }

        var current = byCount[byCount.Count];
        var headPath = HeadPath(storageKey);
        if (!File.Exists(headPath))
        {
            await WriteHeadAsync(current, cancellationToken).ConfigureAwait(false);
            return current;
        }
        var headBytes = await _guard.ReadAllBytesAsync(_root, headPath, 71, "Credential lease head", cancellationToken).ConfigureAwait(false);
        var headHash = Encoding.ASCII.GetString(headBytes);
        if (!IsPrefixedHash(headHash) || !byCount.Values.Any(history => string.Equals(history.Current.ContentHash, headHash, StringComparison.Ordinal)))
        {
            throw new FormatException("Credential lease head is malformed or disconnected.");
        }
        if (!string.Equals(headHash, current.Current.ContentHash, StringComparison.Ordinal))
        {
            await WriteHeadAsync(current, cancellationToken).ConfigureAwait(false);
        }
        return current;
    }

    private async Task<long> CalculateReservedBytesAsync(IEnumerable<string> identities, CancellationToken cancellationToken)
    {
        long reservedBytes = 0;
        foreach (var storageKey in identities)
        {
            var current = await ReadCurrentByStorageKeyAsync(storageKey, cancellationToken).ConfigureAwait(false);
            reservedBytes = checked(reservedBytes + (current is null ? (long)RequiredProtocolVersions * _maximumRecordBytes + HeadBytes : ReservedBytes(current)));
        }
        return reservedBytes;
    }

    private async Task<CredentialLeaseAttemptHistory?> ReadCurrentByStorageKeyAsync(string storageKey, CancellationToken cancellationToken)
    {
        var paths = CurrentVersionPaths(storageKey);
        if (paths.Count == 0)
        {
            return null;
        }

        var byCount = new Dictionary<int, CredentialLeaseAttemptHistory>();
        foreach (var path in paths)
        {
            var bytes = await _guard.ReadAllBytesAsync(_root, path, _maximumRecordBytes, "Credential lease history", cancellationToken).ConfigureAwait(false);
            if (!CredentialLeaseAttemptRecordCodec.TryDecode(bytes, out var history, out _)
                || !string.Equals(StorageKey(history!.Intent.CredentialUseOperationId, history.Intent.CredentialUseGeneration), storageKey, StringComparison.Ordinal)
                || !string.Equals(Path.GetFileName(path), VersionFileName(storageKey, history.Current.ContentHash), StringComparison.Ordinal)
                || !byCount.TryAdd(history.Versions.Count, history))
            {
                throw new FormatException("Credential lease evidence is malformed, forked, or stored under the wrong identity.");
            }
        }
        if (byCount.Count > _maximumVersionsPerAttempt || !byCount.ContainsKey(1) || byCount.Keys.Max() != byCount.Count)
        {
            throw new FormatException("Credential lease evidence is missing an immutable history predecessor.");
        }
        for (var count = 2; count <= byCount.Count; count++)
        {
            if (!IsDirectHistorySuccessor(byCount[count - 1], byCount[count]))
            {
                throw new FormatException("Credential lease evidence is disconnected or forked.");
            }
        }
        return byCount[byCount.Count];
    }

    private long ReservedBytes(CredentialLeaseAttemptHistory history)
    {
        if (IsTerminal(history.Current.Phase))
        {
            return 0;
        }
        if (history.Versions.Count >= RequiredProtocolVersions)
        {
            throw new FormatException("A nonterminal credential lease history exhausted its reserved protocol capacity.");
        }
        return checked((long)(RequiredProtocolVersions - history.Versions.Count) * _maximumRecordBytes);
    }

    private async Task<CredentialLeaseAttemptHistory> ReadHistoryAsync(string path, string expectedStorageKey, string expectedOperationId, long expectedGeneration, CancellationToken cancellationToken)
    {
        var bytes = await _guard.ReadAllBytesAsync(_root, path, _maximumRecordBytes, "Credential lease history", cancellationToken).ConfigureAwait(false);
        if (!CredentialLeaseAttemptRecordCodec.TryDecode(bytes, out var history, out _)
            || !string.Equals(history!.Intent.CredentialUseOperationId, expectedOperationId, StringComparison.Ordinal)
            || history.Intent.CredentialUseGeneration != expectedGeneration
            || !string.Equals(StorageKey(expectedOperationId, expectedGeneration), expectedStorageKey, StringComparison.Ordinal)
            || !string.Equals(Path.GetFileName(path), VersionFileName(expectedStorageKey, history.Current.ContentHash), StringComparison.Ordinal))
        {
            throw new FormatException("Credential lease evidence is malformed, noncanonical, or stored under the wrong identity.");
        }
        return history;
    }

    private async Task WriteImmutableHistoryAsync(CredentialLeaseAttemptHistory history, byte[] encoded, CancellationToken cancellationToken)
    {
        var storageKey = StorageKey(history.Intent.CredentialUseOperationId, history.Intent.CredentialUseGeneration);
        var path = VersionPath(storageKey, history.Current.ContentHash);
        if (File.Exists(path))
        {
            var existing = await ReadHistoryAsync(path, storageKey, history.Intent.CredentialUseOperationId, history.Intent.CredentialUseGeneration, cancellationToken).ConfigureAwait(false);
            if (!HistoriesEqual(existing, history))
            {
                throw new FormatException("Immutable credential lease evidence conflicted with an existing history.");
            }
            return;
        }

        var created = await _guard.WriteTextAtomicallyIfAbsentAsync(_root, path, Encoding.UTF8.GetString(encoded), cancellationToken).ConfigureAwait(false);
        if (!created)
        {
            var existing = await ReadHistoryAsync(path, storageKey, history.Intent.CredentialUseOperationId, history.Intent.CredentialUseGeneration, cancellationToken).ConfigureAwait(false);
            if (!HistoriesEqual(existing, history))
            {
                throw new FormatException("Immutable credential lease evidence conflicted with a concurrent history.");
            }
        }
    }

    private Task WriteHeadAsync(CredentialLeaseAttemptHistory history, CancellationToken cancellationToken)
        => _guard.WriteTextAtomicallyAsync(_root, HeadPath(StorageKey(history.Intent.CredentialUseOperationId, history.Intent.CredentialUseGeneration)), history.Current.ContentHash, cancellationToken);

    private IReadOnlyList<string> CurrentVersionPaths(string storageKey)
        => Directory.EnumerateFiles(_root, "*.json", SearchOption.TopDirectoryOnly)
            .Where(path => TryParseVersionFile(Path.GetFileName(path), out var candidate, out _) && string.Equals(candidate, storageKey, StringComparison.Ordinal))
            .Select(path => _guard.GetFilePath(_root, Path.GetFileName(path)))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private string HeadPath(string storageKey) => _guard.GetFilePath(_root, storageKey + ".head");
    private string VersionPath(string storageKey, string contentHash) => _guard.GetFilePath(_root, VersionFileName(storageKey, contentHash));
    private static string VersionFileName(string storageKey, string contentHash) => $"{storageKey}.{contentHash[7..]}.json";
    private static string OwnerFileName(string storageKey) => storageKey + ".owner";

    private static string StorageKey(string operationId, long generation)
    {
        var material = Encoding.UTF8.GetBytes($"embodysense.credential-lease-attempt-storage.v1\n{operationId}\n{generation}");
        return Convert.ToHexString(SHA256.HashData(material)).ToLowerInvariant();
    }

    private static bool TryCapture(CredentialLeaseAttemptHistory? history, out CredentialLeaseAttemptHistory captured)
    {
        captured = null!;
        try
        {
            if (history is null)
            {
                return false;
            }
            var bytes = CredentialLeaseAttemptRecordCodec.Encode(history);
            if (!CredentialLeaseAttemptRecordCodec.TryDecode(bytes, out var decoded, out _))
            {
                return false;
            }
            captured = decoded!;
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static bool IsDirectHistorySuccessor(CredentialLeaseAttemptHistory current, CredentialLeaseAttemptHistory next)
        => string.Equals(current.Intent.ContentHash, next.Intent.ContentHash, StringComparison.Ordinal)
            && next.Versions.Count == current.Versions.Count + 1
            && current.Versions.SequenceEqual(next.Versions.Take(current.Versions.Count))
            && CredentialLeaseContract.IsDirectSuccessor(current.Intent, current.Current, next.Current);

    private static bool HistoriesEqual(CredentialLeaseAttemptHistory left, CredentialLeaseAttemptHistory right)
        => left.Intent == right.Intent && left.Versions.SequenceEqual(right.Versions);

    private static CredentialLeaseAttemptStoreResult Result(CredentialLeaseAttemptStoreStatus status, CredentialLeaseAttemptHistory? history = null, ICredentialLeaseAttemptLease? lease = null)
    {
        if (history is null)
        {
            return new CredentialLeaseAttemptStoreResult(status, null, lease);
        }
        if (!TryCapture(history, out var captured))
        {
            throw new InvalidOperationException("A validated credential lease store result could not be detached.");
        }
        return new CredentialLeaseAttemptStoreResult(status, captured, lease);
    }

    private static bool IsTerminal(CredentialLeasePhase phase) => phase is CredentialLeasePhase.NotRedeemed or CredentialLeasePhase.Redeemed or CredentialLeasePhase.RedemptionFailed or CredentialLeasePhase.RedemptionAmbiguous;
    private static bool IsId(string? value) => CredentialContractId.TryParse(value, out _, out _);
    private static bool IsHex(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool IsPrefixedHash(string? value) => value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal) && IsHex(value[7..]);

    private static bool TryParseVersionFile(string fileName, out string storageKey, out string contentHash)
    {
        storageKey = string.Empty;
        contentHash = string.Empty;
        if (!fileName.EndsWith(".json", StringComparison.Ordinal))
        {
            return false;
        }
        var candidate = fileName[..^5];
        var separator = candidate.LastIndexOf('.');
        if (separator <= 0 || !IsHex(candidate[..separator]) || !IsHex(candidate[(separator + 1)..]))
        {
            return false;
        }
        storageKey = candidate[..separator];
        contentHash = "sha256:" + candidate[(separator + 1)..];
        return true;
    }

    private static bool TryParseHeadFile(string fileName, out string storageKey)
    {
        storageKey = string.Empty;
        if (!fileName.EndsWith(".head", StringComparison.Ordinal) || !IsHex(fileName[..^5]))
        {
            return false;
        }
        storageKey = fileName[..^5];
        return true;
    }

    private static bool TryParseOwnerFile(string fileName, out string storageKey)
    {
        storageKey = string.Empty;
        if (!fileName.EndsWith(".owner", StringComparison.Ordinal) || !IsHex(fileName[..^6]))
        {
            return false;
        }
        storageKey = fileName[..^6];
        return true;
    }

    private static bool IsInterruptedAtomicWrite(string fileName)
    {
        if (!fileName.StartsWith(".", StringComparison.Ordinal) || !fileName.EndsWith(".tmp", StringComparison.Ordinal))
        {
            return false;
        }
        var withoutMarkers = fileName[1..^4];
        var nonceSeparator = withoutMarkers.LastIndexOf('.');
        if (nonceSeparator <= 0)
        {
            return false;
        }
        var destination = withoutMarkers[..nonceSeparator];
        var nonce = withoutMarkers[(nonceSeparator + 1)..];
        return nonce.Length == 32
            && IsHex(nonce.PadRight(64, '0'))
            && (TryParseVersionFile(destination, out _, out _) || TryParseHeadFile(destination, out _));
    }

    private static bool IsCorrupt(Exception exception) => exception is FormatException or InvalidDataException or OverflowException or ArgumentException;
    private static bool IsUnavailable(Exception exception) => exception is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or NotSupportedException or PlatformNotSupportedException;
}
