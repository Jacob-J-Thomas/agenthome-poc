using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Loops.EffectAttempts;
using EmbodySense.Core.Application.Loops.EffectAttempts.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops.EffectAttempts.Models;

namespace EmbodySense.Core.Persistence.Loops.EffectAttempts;

/// <summary>Persists canonical crash-safe, value-free effect-attempt evidence under stable operation identities.</summary>
/// <remarks>
/// Every immutable attempt version is published before its small atomic head pointer. The first intent is therefore
/// durable before an owner lease is returned, prior evidence is append-only, and an interrupted head publication can be
/// recovered by exact replay. Later head changes require both a live per-generation owner and the common direct-successor
/// relation.
/// </remarks>
public sealed class GovernedLoopEffectAttemptStore : IGovernedLoopEffectAttemptStore, IGovernedLoopEffectAttemptPreparationClaimStore, IGovernedLoopEffectAttemptReadStore
{
    private const int MaximumConfiguredAttempts = 16_384;
    private const int MaximumConfiguredVersionsPerAttempt = 16;
    private const long MaximumConfiguredStoreBytes = 512L * 1024 * 1024;
    private readonly CustomLoopArtifactPathGuard _guard;
    private readonly Guid _instanceId = Guid.NewGuid();
    private readonly int _maximumAttempts;
    private readonly int _maximumRecordBytes;
    private readonly long _maximumStoreBytes;
    private readonly int _maximumVersionsPerAttempt;
    private readonly string _root;
    private readonly string _workspaceId;

    /// <summary>Creates one bounded workspace-scoped effect-attempt store.</summary>
    public GovernedLoopEffectAttemptStore(
        WorkspacePaths paths,
        GovernedLoopEffectAttemptStoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        options ??= new GovernedLoopEffectAttemptStoreOptions();
        if (options.MaxAttempts is < 1 or > MaximumConfiguredAttempts)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Effect-attempt count is outside supported bounds.");
        }
        if (options.MaxRecordUtf8Bytes is < 1 or > GovernedLoopEffectAttemptContractLimits.MaxRecordUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Effect-attempt record bytes are outside supported bounds.");
        }
        if (options.MaxStoreUtf8Bytes is < 1 or > MaximumConfiguredStoreBytes
            || options.MaxStoreUtf8Bytes < options.MaxRecordUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Effect-attempt store bytes are outside supported bounds.");
        }
        if (options.MaxVersionsPerAttempt is < 1 or > MaximumConfiguredVersionsPerAttempt)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Effect-attempt versions per identity are outside supported bounds.");
        }

        _maximumAttempts = options.MaxAttempts;
        _maximumRecordBytes = options.MaxRecordUtf8Bytes;
        _maximumStoreBytes = options.MaxStoreUtf8Bytes;
        _maximumVersionsPerAttempt = options.MaxVersionsPerAttempt;
        _root = paths.GovernedLoopEffectAttemptsPath;
        _workspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
        if (!ContextualRoleWorkspaceId.IsValid(_workspaceId))
        {
            throw new InvalidOperationException("The physical workspace did not produce a canonical workspace scope.");
        }
        _guard = new CustomLoopArtifactPathGuard(paths.RootPath);
    }

    /// <summary>Performs one bounded, non-evidence-mutating readiness probe over the canonical storage envelope.</summary>
    /// <remarks>
    /// The probe may initialize the store's zero-byte coordination lock, but it never creates, claims, resumes, or
    /// advances an effect attempt. It validates the contained non-reparse directory and the complete bounded artifact
    /// inventory while holding the same cross-process lease used by canonical reads and mutations.
    /// </remarks>
    /// <param name="cancellationToken">Cancels bounded lock acquisition and validation.</param>
    /// <returns><see langword="true"/> when the canonical storage envelope is readable and structurally valid; otherwise, <see langword="false"/>.</returns>
    public async Task<bool> ProbeStorageAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var readLock = await _guard.AcquireExclusiveReadLockAsync(_root, cancellationToken).ConfigureAwait(false);
            ValidateDirectory(cancellationToken, out var retainedIdentities, out _);
            foreach (var storageKey in retainedIdentities.Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var versions = VersionPaths(storageKey);
                if (versions.Count == 0)
                {
                    throw new FormatException("Effect-attempt storage contains a head without immutable intent evidence.");
                }
                var identity = await ReadUnboundVersionAsync(versions[0], storageKey, cancellationToken).ConfigureAwait(false);
                _ = await ReadCurrentStrictlyAsync(
                    versions,
                    storageKey,
                    identity.Payload.OperationId,
                    identity.Payload.EffectGeneration,
                    cancellationToken).ConfigureAwait(false);
            }
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
    public async Task<GovernedLoopEffectAttemptStoreResult> ResumeAsync(
        string operationId,
        long effectGeneration,
        CancellationToken cancellationToken = default)
    {
        if (!CustomLoopArtifactIdentifier.IsValid(operationId, GovernedLoopExecutionLimits.MaxIdentifierCharacters)
            || effectGeneration is < 1 or > GovernedLoopExecutionLimits.MaxVersion)
        {
            return Result(GovernedLoopEffectAttemptStoreStatus.Corrupt);
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var mutation = await AcquireMutationLockAsync(cancellationToken).ConfigureAwait(false);
            ValidateDirectory(out _, out var retainedBytes);
            var storageKey = StorageKey(operationId, effectGeneration);
            if (!File.Exists(HeadPath(storageKey))
                && VersionPaths(storageKey).Count > 0
                && retainedBytes > _maximumStoreBytes - GovernedLoopExecutionLimits.Sha256HexCharacters)
            {
                return Result(GovernedLoopEffectAttemptStoreStatus.Backpressured);
            }
            var current = await ReadCurrentOrOrphanAsync(storageKey, operationId, effectGeneration, cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                return Result(GovernedLoopEffectAttemptStoreStatus.NotFound);
            }
            if (DoesNotRequireOwner(current.Payload.Phase))
            {
                return Result(GovernedLoopEffectAttemptStoreStatus.Replayed, current);
            }
            var lease = TryAcquireOwner(operationId, effectGeneration);
            return lease is null
                ? Result(GovernedLoopEffectAttemptStoreStatus.OperationInProgress, current)
                : Result(GovernedLoopEffectAttemptStoreStatus.Replayed, current, lease);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsCorrupt(exception))
        {
            return Result(GovernedLoopEffectAttemptStoreStatus.Corrupt);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return Result(GovernedLoopEffectAttemptStoreStatus.Unavailable);
        }
    }

    /// <inheritdoc />
    public async Task<GovernedLoopEffectAttemptReadResult> ReadAsync(
        string workspaceId,
        string operationId,
        long effectGeneration,
        CancellationToken cancellationToken = default)
    {
        if (!ContextualRoleWorkspaceId.IsValid(workspaceId)
            || !string.Equals(workspaceId, _workspaceId, StringComparison.Ordinal))
        {
            return ReadResult(GovernedLoopEffectAttemptReadStatus.Unavailable);
        }
        if (!CustomLoopArtifactIdentifier.IsValid(operationId, GovernedLoopExecutionLimits.MaxIdentifierCharacters)
            || effectGeneration is < 1 or > GovernedLoopExecutionLimits.MaxVersion)
        {
            return ReadResult(GovernedLoopEffectAttemptReadStatus.Corrupt);
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!_guard.DirectoryExists(_root))
            {
                return ReadResult(GovernedLoopEffectAttemptReadStatus.Missing);
            }

            var lockPath = _guard.GetFilePath(_root, ".custom-loop-mutations.lock");
            using var readLock = await AcquireExistingReadLockAsync(lockPath, cancellationToken).ConfigureAwait(false);
            if (readLock is null)
            {
                return ReadResult(GovernedLoopEffectAttemptReadStatus.Corrupt);
            }
            ValidateDirectory(out _, out _);
            var storageKey = StorageKey(operationId, effectGeneration);
            var versions = VersionPaths(storageKey);
            if (versions.Count == 0)
            {
                return ReadResult(GovernedLoopEffectAttemptReadStatus.Missing);
            }

            var current = await ReadCurrentStrictlyAsync(versions, storageKey, operationId, effectGeneration, cancellationToken).ConfigureAwait(false);
            return ReadResult(GovernedLoopEffectAttemptReadStatus.Current, current);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsCorrupt(exception))
        {
            return ReadResult(GovernedLoopEffectAttemptReadStatus.Corrupt);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return ReadResult(GovernedLoopEffectAttemptReadStatus.Unavailable);
        }
    }

    /// <inheritdoc />
    public Task<GovernedLoopEffectAttemptStoreResult> BeginAsync(
        GovernedLoopEffectAttempt prepared,
        CancellationToken cancellationToken = default)
        => BeginCoreAsync(prepared, null, cancellationToken);

    /// <inheritdoc />
    public Task<GovernedLoopEffectAttemptStoreResult> BeginWithPreparationClaimAsync(
        GovernedLoopEffectAttempt prepared,
        Func<CancellationToken, Task<bool>> preparationClaim,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparationClaim);
        return BeginCoreAsync(prepared, preparationClaim, cancellationToken);
    }

    private async Task<GovernedLoopEffectAttemptStoreResult> BeginCoreAsync(
        GovernedLoopEffectAttempt prepared,
        Func<CancellationToken, Task<bool>>? preparationClaim,
        CancellationToken cancellationToken)
    {
        if (!TryCapture(prepared, out var captured)
            || captured.Payload.Phase != GovernedLoopEffectPhase.IntentPrepared
            || captured.DispatchAuthorityEvidenceHash is not null
            || captured.PreviousContentHash is not null)
        {
            return Result(GovernedLoopEffectAttemptStoreStatus.Corrupt);
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var mutation = await AcquireMutationLockAsync(cancellationToken).ConfigureAwait(false);
            ValidateDirectory(out var retainedIdentities, out var retainedBytes);
            var storageKey = StorageKey(captured.Payload.OperationId, captured.Payload.EffectGeneration);
            if (!File.Exists(HeadPath(storageKey))
                && retainedBytes > _maximumStoreBytes - GovernedLoopExecutionLimits.Sha256HexCharacters)
            {
                return Result(GovernedLoopEffectAttemptStoreStatus.Backpressured);
            }
            var current = await ReadCurrentOrOrphanAsync(
                storageKey,
                captured.Payload.OperationId,
                captured.Payload.EffectGeneration,
                cancellationToken).ConfigureAwait(false);
            if (current is not null)
            {
                if (!GovernedLoopEffectAttemptContract.HasSameIntent(captured, current))
                {
                    return Result(GovernedLoopEffectAttemptStoreStatus.Conflict, current);
                }
                if (!File.Exists(HeadPath(storageKey)))
                {
                    if (retainedBytes > _maximumStoreBytes - GovernedLoopExecutionLimits.Sha256HexCharacters)
                    {
                        return Result(GovernedLoopEffectAttemptStoreStatus.Backpressured, current);
                    }
                    await WriteHeadAsync(current, cancellationToken).ConfigureAwait(false);
                }
                if (DoesNotRequireOwner(current.Payload.Phase))
                {
                    return Result(GovernedLoopEffectAttemptStoreStatus.Replayed, current);
                }

                var replayLease = TryAcquireOwner(current.Payload.OperationId, current.Payload.EffectGeneration);
                return replayLease is null
                    ? Result(GovernedLoopEffectAttemptStoreStatus.OperationInProgress, current)
                    : Result(GovernedLoopEffectAttemptStoreStatus.Replayed, current, replayLease);
            }

            var encoded = GovernedLoopEffectAttemptRecordCodec.Encode(captured);
            if (!retainedIdentities.Contains(storageKey) && retainedIdentities.Count >= _maximumAttempts
                || encoded.Length > _maximumRecordBytes
                || retainedBytes > _maximumStoreBytes - encoded.Length - GovernedLoopExecutionLimits.Sha256HexCharacters)
            {
                return Result(GovernedLoopEffectAttemptStoreStatus.Backpressured);
            }
            if (preparationClaim is not null
                && !await preparationClaim(cancellationToken).ConfigureAwait(false))
            {
                return Result(GovernedLoopEffectAttemptStoreStatus.PreparationExpired);
            }

            var lease = TryAcquireOwner(captured.Payload.OperationId, captured.Payload.EffectGeneration);
            if (lease is null)
            {
                return Result(GovernedLoopEffectAttemptStoreStatus.OperationInProgress);
            }
            try
            {
                await WriteImmutableVersionAsync(captured, encoded, cancellationToken).ConfigureAwait(false);
                await WriteHeadAsync(captured, cancellationToken).ConfigureAwait(false);
                return Result(GovernedLoopEffectAttemptStoreStatus.Created, captured, lease);
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
            return Result(GovernedLoopEffectAttemptStoreStatus.Corrupt);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return Result(GovernedLoopEffectAttemptStoreStatus.Unavailable);
        }
    }

    /// <inheritdoc />
    public async Task<GovernedLoopEffectAttemptStoreResult> CompareExchangeAsync(
        string expectedContentHash,
        GovernedLoopEffectAttempt replacement,
        IGovernedLoopEffectAttemptLease lease,
        CancellationToken cancellationToken = default)
    {
        if (!IsHash(expectedContentHash) || !TryCapture(replacement, out var captured))
        {
            return Result(GovernedLoopEffectAttemptStoreStatus.Corrupt);
        }
        if (lease is not GovernedLoopEffectAttemptLease owned
            || !owned.Owns(_instanceId, captured.Payload.OperationId, captured.Payload.EffectGeneration))
        {
            return Result(GovernedLoopEffectAttemptStoreStatus.Conflict);
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var mutation = await AcquireMutationLockAsync(cancellationToken).ConfigureAwait(false);
            ValidateDirectory(out _, out var retainedBytes);
            var storageKey = StorageKey(captured.Payload.OperationId, captured.Payload.EffectGeneration);
            if (!File.Exists(HeadPath(storageKey))
                && VersionPaths(storageKey).Count > 0
                && retainedBytes > _maximumStoreBytes - GovernedLoopExecutionLimits.Sha256HexCharacters)
            {
                return Result(GovernedLoopEffectAttemptStoreStatus.Backpressured);
            }
            var current = await ReadCurrentAsync(
                storageKey,
                captured.Payload.OperationId,
                captured.Payload.EffectGeneration,
                cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                return Result(GovernedLoopEffectAttemptStoreStatus.Conflict);
            }
            if (!string.Equals(current.ContentHash, expectedContentHash, StringComparison.Ordinal))
            {
                return Result(GovernedLoopEffectAttemptStoreStatus.Conflict, current);
            }
            if (string.Equals(current.ContentHash, captured.ContentHash, StringComparison.Ordinal))
            {
                return Result(GovernedLoopEffectAttemptStoreStatus.Replayed, current);
            }
            if (!GovernedLoopEffectAttemptContract.IsDirectSuccessor(current, captured))
            {
                return Result(GovernedLoopEffectAttemptStoreStatus.Conflict, current);
            }

            var encoded = GovernedLoopEffectAttemptRecordCodec.Encode(captured);
            var versionPath = VersionPath(storageKey, captured.ContentHash);
            var versionExists = File.Exists(versionPath);
            var operationVersions = VersionPaths(storageKey);
            if (!versionExists && operationVersions.Count >= _maximumVersionsPerAttempt
                || encoded.Length > _maximumRecordBytes
                || !versionExists && retainedBytes > _maximumStoreBytes - encoded.Length)
            {
                return Result(GovernedLoopEffectAttemptStoreStatus.Backpressured, current);
            }

            await WriteImmutableVersionAsync(captured, encoded, cancellationToken).ConfigureAwait(false);
            await WriteHeadAsync(captured, cancellationToken).ConfigureAwait(false);
            return Result(GovernedLoopEffectAttemptStoreStatus.Created, captured);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsCorrupt(exception))
        {
            return Result(GovernedLoopEffectAttemptStoreStatus.Corrupt);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return Result(GovernedLoopEffectAttemptStoreStatus.Unavailable);
        }
    }

    internal async Task<(bool EvidenceComplete, int RemovedCount)> TryCleanupUnreferencedBeforeEvidenceAsync(
        IReadOnlyList<string> beforeEvidenceIds,
        int maximumRemovals,
        Func<string, CancellationToken, Task<bool>> cleanup,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(beforeEvidenceIds);
        ArgumentNullException.ThrowIfNull(cleanup);
        if (beforeEvidenceIds.Count > MaximumConfiguredAttempts
            || maximumRemovals is < 1 or > 64
            || beforeEvidenceIds.Distinct(StringComparer.Ordinal).Count() != beforeEvidenceIds.Count
            || beforeEvidenceIds.Any(beforeEvidenceId =>
                !beforeEvidenceId.StartsWith("before-", StringComparison.Ordinal)
                || !IsHash(beforeEvidenceId["before-".Length..])))
        {
            return (false, 0);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var removed = 0;
        try
        {
            using var mutation = await AcquireMutationLockAsync(cancellationToken).ConfigureAwait(false);
            ValidateDirectory(out var retainedIdentities, out _);
            var referenced = new HashSet<string>(StringComparer.Ordinal);
            foreach (var storageKey in retainedIdentities.Order(StringComparer.Ordinal))
            {
                var versions = VersionPaths(storageKey);
                if (versions.Count == 0)
                {
                    throw new FormatException("Effect-attempt storage contains a head without immutable intent evidence.");
                }
                var identity = await ReadUnboundVersionAsync(versions[0], storageKey, cancellationToken).ConfigureAwait(false);
                var current = await ReadAndRecoverCompleteGraphAsync(
                    versions,
                    storageKey,
                    identity.Payload.OperationId,
                    identity.Payload.EffectGeneration,
                    cancellationToken).ConfigureAwait(false);
                if (current.BeforeEvidenceId is not null)
                {
                    referenced.Add(current.BeforeEvidenceId);
                }
            }
            foreach (var beforeEvidenceId in beforeEvidenceIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (referenced.Contains(beforeEvidenceId))
                {
                    continue;
                }
                if (!await cleanup(beforeEvidenceId, cancellationToken).ConfigureAwait(false))
                {
                    return (false, removed);
                }
                removed++;
                if (removed == maximumRemovals)
                {
                    break;
                }
            }
            return (true, removed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsCorrupt(exception) || IsUnavailable(exception))
        {
            return (false, removed);
        }
    }

    private GovernedLoopEffectAttemptLease? TryAcquireOwner(string operationId, long effectGeneration)
    {
        var path = _guard.GetFilePath(_root, OwnerFileName(StorageKey(operationId, effectGeneration)));
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.WriteThrough);
            if (stream.Length != 0)
            {
                stream.Dispose();
                throw new FormatException("Effect-attempt ownership evidence must remain value-free.");
            }
            return new GovernedLoopEffectAttemptLease(_instanceId, operationId, effectGeneration, stream);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
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

    private static async Task<FileStream?> AcquireExistingReadLockAsync(string lockPath, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.None, bufferSize: 1, FileOptions.WriteThrough);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Yield();
            }
        }

        throw new IOException("Governed-loop effect-attempt persistence remained locked by another process after bounded read retries.");
    }

    private void ValidateDirectory(out HashSet<string> retainedIdentities, out long retainedBytes)
        => ValidateDirectory(CancellationToken.None, out retainedIdentities, out retainedBytes);

    private void ValidateDirectory(CancellationToken cancellationToken, out HashSet<string> retainedIdentities, out long retainedBytes)
    {
        var maximumArtifacts = checked(MaximumConfiguredAttempts * (MaximumConfiguredVersionsPerAttempt + 2) + 2);
        var entries = new List<string>(Math.Min(maximumArtifacts, 1024));
        foreach (var entry in Directory.EnumerateFileSystemEntries(_root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Add(entry);
            if (entries.Count > maximumArtifacts)
            {
                throw new FormatException("Effect-attempt storage exceeds its finite artifact bounds.");
            }
        }

        if (entries.Any(Directory.Exists))
        {
            throw new FormatException("Effect-attempt storage exceeds its finite artifact bounds.");
        }

        var identities = new HashSet<string>(StringComparer.Ordinal);
        var versionCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        retainedBytes = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(entry);
            if (fileName == ".custom-loop-mutations.lock")
            {
                continue;
            }
            if (IsInterruptedAtomicWrite(fileName))
            {
                throw new FormatException("Effect-attempt storage contains an interrupted atomic publication that requires operator inspection.");
            }
            if (TryParseVersionFile(fileName, out var versionStorageKey, out _))
            {
                identities.Add(versionStorageKey);
                versionCounts[versionStorageKey] = checked(versionCounts.GetValueOrDefault(versionStorageKey) + 1);
                if (versionCounts[versionStorageKey] > _maximumVersionsPerAttempt)
                {
                    throw new FormatException("Effect-attempt storage contains too many immutable versions for one operation.");
                }
                retainedBytes = checked(retainedBytes + _guard.GetFileLength(_root, entry));
                continue;
            }
            if (TryParseHeadFile(fileName, out var headStorageKey))
            {
                identities.Add(headStorageKey);
                retainedBytes = checked(retainedBytes + _guard.GetFileLength(_root, entry));
                continue;
            }
            if (TryParseOwnerFile(fileName, out var ownerStorageKey))
            {
                if (_guard.GetFileLength(_root, entry) != 0)
                {
                    throw new FormatException("Effect-attempt ownership evidence is conflicting or not value-free.");
                }
                continue;
            }

            throw new FormatException("Effect-attempt storage contains an unsupported artifact.");
        }
        retainedIdentities = identities;
    }

    private static bool IsInterruptedAtomicWrite(string fileName)
    {
        if (!fileName.StartsWith(".", StringComparison.Ordinal)
            || !fileName.EndsWith(".tmp", StringComparison.Ordinal))
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
        return (TryParseVersionFile(destination, out _, out _) || TryParseHeadFile(destination, out _))
            && nonce.Length == 32
            && nonce.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

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
        if (separator <= 0
            || !IsHash(candidate[..separator])
            || !IsHash(candidate[(separator + 1)..]))
        {
            return false;
        }
        storageKey = candidate[..separator];
        contentHash = candidate[(separator + 1)..];
        return true;
    }

    private static bool TryParseHeadFile(string fileName, out string storageKey)
    {
        storageKey = string.Empty;
        if (!fileName.EndsWith(".head", StringComparison.Ordinal))
        {
            return false;
        }
        var candidate = fileName[..^5];
        if (!IsHash(candidate))
        {
            return false;
        }
        storageKey = candidate;
        return true;
    }

    private static bool TryParseOwnerFile(string fileName, out string storageKey)
    {
        storageKey = string.Empty;
        if (!fileName.EndsWith(".owner", StringComparison.Ordinal))
        {
            return false;
        }
        var candidate = fileName[..^6];
        if (!IsHash(candidate))
        {
            return false;
        }
        storageKey = candidate;
        return true;
    }

    private async Task<GovernedLoopEffectAttempt?> ReadCurrentOrOrphanAsync(
        string storageKey,
        string operationId,
        long effectGeneration,
        CancellationToken cancellationToken)
    {
        var versions = VersionPaths(storageKey);
        if (versions.Count == 0)
        {
            return null;
        }
        return await ReadAndRecoverCompleteGraphAsync(
            versions,
            storageKey,
            operationId,
            effectGeneration,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<GovernedLoopEffectAttempt?> ReadCurrentAsync(
        string storageKey,
        string operationId,
        long effectGeneration,
        CancellationToken cancellationToken)
    {
        var versions = VersionPaths(storageKey);
        if (versions.Count == 0)
        {
            return null;
        }
        return await ReadAndRecoverCompleteGraphAsync(
            versions,
            storageKey,
            operationId,
            effectGeneration,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<GovernedLoopEffectAttempt> ReadAndRecoverCompleteGraphAsync(
        IReadOnlyList<string> versionPaths,
        string storageKey,
        string operationId,
        long effectGeneration,
        CancellationToken cancellationToken)
    {
        if (versionPaths.Count > _maximumVersionsPerAttempt)
        {
            throw new FormatException("Governed-loop effect-attempt evidence exceeds its finite chain bound.");
        }
        var versions = new Dictionary<string, GovernedLoopEffectAttempt>(StringComparer.Ordinal);
        foreach (var versionPath in versionPaths)
        {
            var version = await ReadVersionAsync(
                versionPath,
                storageKey,
                operationId,
                effectGeneration,
                cancellationToken).ConfigureAwait(false);
            if (!versions.TryAdd(version.ContentHash, version))
            {
                throw new FormatException("Governed-loop effect-attempt evidence contains a duplicate immutable version.");
            }
        }

        var roots = versions.Values.Where(version => version.PreviousContentHash is null).ToArray();
        if (roots.Length != 1)
        {
            throw new FormatException("Governed-loop effect-attempt evidence must contain exactly one initial intent root.");
        }
        var children = new Dictionary<string, GovernedLoopEffectAttempt>(StringComparer.Ordinal);
        foreach (var version in versions.Values.Where(version => version.PreviousContentHash is not null))
        {
            if (!versions.TryGetValue(version.PreviousContentHash!, out var prior)
                || !GovernedLoopEffectAttemptContract.IsDirectSuccessor(prior, version)
                || !children.TryAdd(prior.ContentHash, version))
            {
                throw new FormatException("Governed-loop effect-attempt evidence contains a missing predecessor, broken successor, or fork.");
            }
        }

        var current = roots[0];
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (visited.Add(current.ContentHash) && children.TryGetValue(current.ContentHash, out var child))
        {
            current = child;
        }
        if (visited.Count != versions.Count)
        {
            throw new FormatException("Governed-loop effect-attempt evidence contains a cycle or disconnected immutable version.");
        }

        var headPath = HeadPath(storageKey);
        if (!File.Exists(headPath))
        {
            await WriteHeadAsync(current, cancellationToken).ConfigureAwait(false);
            return current;
        }
        var headBytes = await _guard.ReadAllBytesAsync(
            _root,
            headPath,
            GovernedLoopExecutionLimits.Sha256HexCharacters,
            "Governed-loop effect-attempt head",
            cancellationToken).ConfigureAwait(false);
        var headHash = Encoding.ASCII.GetString(headBytes);
        if (!IsHash(headHash) || !visited.Contains(headHash))
        {
            throw new FormatException("Governed-loop effect-attempt head is malformed, noncanonical, or disconnected from retained evidence.");
        }
        if (!string.Equals(headHash, current.ContentHash, StringComparison.Ordinal))
        {
            await WriteHeadAsync(current, cancellationToken).ConfigureAwait(false);
        }
        return current;
    }

    private async Task<GovernedLoopEffectAttempt> ReadCurrentStrictlyAsync(
        IReadOnlyList<string> versionPaths,
        string storageKey,
        string operationId,
        long effectGeneration,
        CancellationToken cancellationToken)
    {
        if (versionPaths.Count > _maximumVersionsPerAttempt)
        {
            throw new FormatException("Governed-loop effect-attempt evidence exceeds its finite chain bound.");
        }

        var versions = new Dictionary<string, GovernedLoopEffectAttempt>(StringComparer.Ordinal);
        foreach (var versionPath in versionPaths)
        {
            var version = await ReadVersionAsync(versionPath, storageKey, operationId, effectGeneration, cancellationToken).ConfigureAwait(false);
            if (!versions.TryAdd(version.ContentHash, version))
            {
                throw new FormatException("Governed-loop effect-attempt evidence contains a duplicate immutable version.");
            }
        }

        var roots = versions.Values.Where(version => version.PreviousContentHash is null).ToArray();
        if (roots.Length != 1)
        {
            throw new FormatException("Governed-loop effect-attempt evidence must contain exactly one initial intent root.");
        }

        var children = new Dictionary<string, GovernedLoopEffectAttempt>(StringComparer.Ordinal);
        foreach (var version in versions.Values.Where(version => version.PreviousContentHash is not null))
        {
            if (!versions.TryGetValue(version.PreviousContentHash!, out var prior)
                || !GovernedLoopEffectAttemptContract.IsDirectSuccessor(prior, version)
                || !children.TryAdd(prior.ContentHash, version))
            {
                throw new FormatException("Governed-loop effect-attempt evidence contains a missing predecessor, broken successor, or fork.");
            }
        }

        var current = roots[0];
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (visited.Add(current.ContentHash) && children.TryGetValue(current.ContentHash, out var child))
        {
            current = child;
        }
        if (visited.Count != versions.Count)
        {
            throw new FormatException("Governed-loop effect-attempt evidence contains a cycle or disconnected immutable version.");
        }

        var headPath = HeadPath(storageKey);
        if (!File.Exists(headPath))
        {
            throw new FormatException("Governed-loop effect-attempt head is missing and cannot be repaired by a read-only observation.");
        }

        var headBytes = await _guard.ReadAllBytesAsync(
            _root,
            headPath,
            GovernedLoopExecutionLimits.Sha256HexCharacters,
            "Governed-loop effect-attempt head",
            cancellationToken).ConfigureAwait(false);
        var headHash = Encoding.ASCII.GetString(headBytes);
        if (!IsHash(headHash) || !visited.Contains(headHash) || !string.Equals(headHash, current.ContentHash, StringComparison.Ordinal))
        {
            throw new FormatException("Governed-loop effect-attempt head is malformed, noncanonical, or disconnected from retained evidence.");
        }

        return current;
    }

    private async Task<GovernedLoopEffectAttempt> ReadVersionAsync(
        string path,
        string expectedStorageKey,
        string expectedOperationId,
        long expectedEffectGeneration,
        CancellationToken cancellationToken)
    {
        var bytes = await _guard.ReadAllBytesAsync(
            _root,
            path,
            _maximumRecordBytes,
            "Governed-loop effect-attempt version",
            cancellationToken).ConfigureAwait(false);
        if (!GovernedLoopEffectAttemptRecordCodec.TryDecode(bytes, out var attempt, out _)
            || !string.Equals(attempt!.Payload.OperationId, expectedOperationId, StringComparison.Ordinal)
            || attempt.Payload.EffectGeneration != expectedEffectGeneration
            || !string.Equals(StorageKey(attempt.Payload.OperationId, attempt.Payload.EffectGeneration), expectedStorageKey, StringComparison.Ordinal)
            || !string.Equals(Path.GetFileName(path), VersionFileName(expectedStorageKey, attempt.ContentHash), StringComparison.Ordinal))
        {
            throw new FormatException("Governed-loop effect-attempt evidence is malformed, noncanonical, or stored under the wrong identity.");
        }
        return attempt;
    }

    private async Task<GovernedLoopEffectAttempt> ReadUnboundVersionAsync(
        string path,
        string expectedStorageKey,
        CancellationToken cancellationToken)
    {
        var bytes = await _guard.ReadAllBytesAsync(
            _root,
            path,
            _maximumRecordBytes,
            "Governed-loop effect-attempt version",
            cancellationToken).ConfigureAwait(false);
        if (!GovernedLoopEffectAttemptRecordCodec.TryDecode(bytes, out var attempt, out _)
            || !string.Equals(StorageKey(attempt!.Payload.OperationId, attempt.Payload.EffectGeneration), expectedStorageKey, StringComparison.Ordinal)
            || !string.Equals(Path.GetFileName(path), VersionFileName(expectedStorageKey, attempt.ContentHash), StringComparison.Ordinal))
        {
            throw new FormatException("Governed-loop effect-attempt evidence is malformed, noncanonical, or stored under the wrong identity.");
        }
        return attempt;
    }

    private async Task WriteImmutableVersionAsync(
        GovernedLoopEffectAttempt attempt,
        byte[] encoded,
        CancellationToken cancellationToken)
    {
        if (encoded.Length > _maximumRecordBytes)
        {
            throw new FormatException("Governed-loop effect-attempt evidence exceeds its configured byte bound.");
        }
        var storageKey = StorageKey(attempt.Payload.OperationId, attempt.Payload.EffectGeneration);
        var path = VersionPath(storageKey, attempt.ContentHash);
        if (File.Exists(path))
        {
            var existing = await ReadVersionAsync(
                path,
                storageKey,
                attempt.Payload.OperationId,
                attempt.Payload.EffectGeneration,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(existing.ContentHash, attempt.ContentHash, StringComparison.Ordinal))
            {
                throw new FormatException("Immutable effect-attempt evidence conflicted with an existing version.");
            }
            return;
        }
        var created = await _guard.WriteTextAtomicallyIfAbsentAsync(
            _root,
            path,
            Encoding.UTF8.GetString(encoded),
            cancellationToken).ConfigureAwait(false);
        if (!created)
        {
            _ = await ReadVersionAsync(
                path,
                storageKey,
                attempt.Payload.OperationId,
                attempt.Payload.EffectGeneration,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private Task WriteHeadAsync(GovernedLoopEffectAttempt attempt, CancellationToken cancellationToken)
        => _guard.WriteTextAtomicallyAsync(
            _root,
            HeadPath(StorageKey(attempt.Payload.OperationId, attempt.Payload.EffectGeneration)),
            attempt.ContentHash,
            cancellationToken);

    private IReadOnlyList<string> VersionPaths(string storageKey)
        => Directory.EnumerateFiles(_root, "*.json", SearchOption.TopDirectoryOnly)
            .Where(path => TryParseVersionFile(Path.GetFileName(path), out var candidate, out _)
                && string.Equals(candidate, storageKey, StringComparison.Ordinal))
            .Select(path => _guard.GetFilePath(_root, Path.GetFileName(path)))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private string HeadPath(string storageKey) => _guard.GetFilePath(_root, storageKey + ".head");

    private string VersionPath(string storageKey, string contentHash)
        => _guard.GetFilePath(_root, VersionFileName(storageKey, contentHash));

    private static string VersionFileName(string storageKey, string contentHash)
        => $"{storageKey}.{contentHash}.json";

    private static string OwnerFileName(string storageKey) => storageKey + ".owner";

    private static string StorageKey(string operationId, long effectGeneration)
    {
        var material = Encoding.UTF8.GetBytes($"embodysense.governed-loop-effect-attempt-storage.v1\n{operationId}\n{effectGeneration}");
        return Convert.ToHexString(SHA256.HashData(material)).ToLowerInvariant();
    }

    private static bool TryCapture(GovernedLoopEffectAttempt? source, out GovernedLoopEffectAttempt captured)
    {
        captured = null!;
        if (source is null)
        {
            return false;
        }
        try
        {
            var bytes = GovernedLoopEffectAttemptRecordCodec.Encode(source);
            if (!GovernedLoopEffectAttemptRecordCodec.TryDecode(bytes, out var decoded, out _))
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

    private static bool DoesNotRequireOwner(GovernedLoopEffectPhase phase)
        => phase is GovernedLoopEffectPhase.DispatchNotStarted
            or GovernedLoopEffectPhase.Committed
            or GovernedLoopEffectPhase.ReconciliationRequired
            or GovernedLoopEffectPhase.Reconciled;

    private static GovernedLoopEffectAttemptStoreResult Result(
        GovernedLoopEffectAttemptStoreStatus status,
        GovernedLoopEffectAttempt? attempt = null,
        IGovernedLoopEffectAttemptLease? lease = null)
    {
        if (attempt is null)
        {
            return new GovernedLoopEffectAttemptStoreResult(status, null, lease);
        }
        if (!TryCapture(attempt, out var captured))
        {
            throw new InvalidOperationException("A validated effect-attempt store result could not be detached.");
        }
        return new GovernedLoopEffectAttemptStoreResult(status, captured, lease);
    }

    private static GovernedLoopEffectAttemptReadResult ReadResult(
        GovernedLoopEffectAttemptReadStatus status,
        GovernedLoopEffectAttempt? attempt = null)
    {
        if (attempt is null)
        {
            return new GovernedLoopEffectAttemptReadResult(status);
        }
        if (!TryCapture(attempt, out var captured))
        {
            throw new InvalidOperationException("A validated effect-attempt read result could not be detached.");
        }
        return new GovernedLoopEffectAttemptReadResult(status, captured);
    }

    private static bool IsHash(string? value)
        => value is { Length: GovernedLoopExecutionLimits.Sha256HexCharacters }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsCorrupt(Exception exception)
        => exception is FormatException or InvalidDataException or OverflowException or ArgumentException;

    private static bool IsUnavailable(Exception exception)
        => exception is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or NotSupportedException or PlatformNotSupportedException;
}
