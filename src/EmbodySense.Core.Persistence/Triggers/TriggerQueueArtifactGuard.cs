using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Triggers.Models;
using Microsoft.Win32.SafeHandles;

namespace EmbodySense.Core.Persistence.Triggers;

/// <summary>Provides contained, immutable-generation, cross-process serialized trigger queue artifact access.</summary>
internal sealed class TriggerQueueArtifactGuard
{
    private const int MaxDiscoveredGenerations = 2;
    private const int MaxDirectoryEntries = 128;
    private static readonly SemaphoreSlim[] _processLocks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private static readonly TimeSpan _lockTimeout = TimeSpan.FromSeconds(15);
    private readonly string _workspaceRoot;
    private readonly string _queueRoot;
    private readonly StringComparison _comparison;
    private readonly int _maxTombstoneArtifacts;
    private readonly bool _recycleAuthenticatedTombstones;

    /// <summary>Initializes the guard for one workspace and queue root.</summary>
    public TriggerQueueArtifactGuard(
        string workspaceRoot,
        string queueRoot,
        int maxTombstoneArtifacts,
        bool recycleAuthenticatedTombstones = false)
    {
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _queueRoot = Path.GetFullPath(queueRoot);
        _comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        _maxTombstoneArtifacts = maxTombstoneArtifacts;
        _recycleAuthenticatedTombstones = recycleAuthenticatedTombstones;
        EnsureContained(_workspaceRoot, _queueRoot);
    }

    /// <summary>Gets and validates a direct child path.</summary>
    public string GetPath(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var path = Path.GetFullPath(Path.Combine(_queueRoot, fileName));
        EnsureContained(_queueRoot, path);
        return path;
    }

    /// <summary>Acquires the one workspace queue mutation lease after validating its path and handle identity.</summary>
    public async Task<TriggerQueueMutationLease> AcquireMutationLockAsync(ITriggerQueueDurabilityObserver observer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observer);
        PrepareRoot();
        var rootSnapshot = CaptureRootSnapshot();
        var path = GetPath(".queue.lock");
        var hash = (OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).GetHashCode(path);
        var processLock = _processLocks[(hash & int.MaxValue) % _processLocks.Length];
        await processLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var wait = Stopwatch.StartNew();
        TriggerQueueDirectoryAuthority? directoryAuthority = null;
        try
        {
            directoryAuthority = TriggerQueueDirectoryAuthority.Capture(rootSnapshot);
            observer.OnMutationDirectoryBound(_queueRoot);
            directoryAuthority.ValidateCurrentPath();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileStream? stream = null;
                try
                {
                    stream = directoryAuthority.OpenOrCreate(Path.GetFileName(path));
                    var handleIdentity = TriggerQueueNativeFileInspector.InspectHandle(stream.SafeFileHandle, path);
                    if (handleIdentity != TriggerQueueNativeFileInspector.InspectPath(path))
                    {
                        throw new InvalidOperationException("Trigger queue mutation lock path was substituted while opening it.");
                    }

                    if (CustomLoopCrossProcessFileLock.TryAcquire(stream))
                    {
                        if (TriggerQueueNativeFileInspector.InspectHandle(stream.SafeFileHandle, path) != TriggerQueueNativeFileInspector.InspectPath(path))
                        {
                            throw new InvalidOperationException("Trigger queue mutation lock path was substituted after acquisition.");
                        }

                        ValidateRootSnapshot(rootSnapshot);
                        return new TriggerQueueMutationLease(stream, processLock, directoryAuthority, rootSnapshot, path, handleIdentity);
                    }
                }
                catch (IOException)
                {
                    stream?.Dispose();
                }
                catch
                {
                    stream?.Dispose();
                    throw;
                }

                stream?.Dispose();
                if (wait.Elapsed >= _lockTimeout)
                {
                    throw new TimeoutException("Trigger queue mutation lock remained busy beyond the bounded acquisition interval.");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            directoryAuthority?.Dispose();
            processLock.Release();
            throw;
        }
    }

    /// <summary>Discovers a bounded set of immutable generations and reads the exact latest artifact.</summary>
    public async Task<TriggerQueueReadResult> ReadLatestAsync(int maximumBytes, ITriggerQueueDurabilityObserver observer, TriggerQueueMutationLease mutationLease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observer);
        ArgumentNullException.ThrowIfNull(mutationLease);
        ValidateMutationLease(mutationLease);
        var artifacts = new List<TriggerQueueArtifactSnapshot>();
        var cleanupArtifacts = new List<TriggerQueueArtifactSnapshot>();
        var abandonedArtifacts = new List<TriggerQueueArtifactSnapshot>();
        var precursorArtifacts = new List<TriggerQueueArtifactSnapshot>();
        var tombstones = new List<TriggerQueueArtifactSnapshot>();
        var entries = Directory.EnumerateFileSystemEntries(_queueRoot, "*", SearchOption.TopDirectoryOnly).Take(MaxDirectoryEntries + 1).ToArray();
        if (entries.Length > MaxDirectoryEntries)
        {
            throw new InvalidOperationException($"Trigger queue persistence refuses more than {MaxDirectoryEntries} directory entries.");
        }

        long aggregateBytes = 0;
        var aggregateLimit = checked((long)maximumBytes * 2 + 4_096);
        foreach (var path in entries)
        {
            var name = Path.GetFileName(path);
            if (string.Equals(name, ".queue.lock", StringComparison.Ordinal))
            {
                var identity = TriggerQueueNativeFileInspector.InspectPath(path);
                aggregateBytes = checked(aggregateBytes + new FileInfo(path).Length);
                continue;
            }

            if (TryParseLedgerName(name, out var generation))
            {
                if (artifacts.Count == MaxDiscoveredGenerations)
                {
                    throw new InvalidOperationException($"Trigger queue persistence refuses more than {MaxDiscoveredGenerations} discovered ledger generations.");
                }

                var identity = TriggerQueueNativeFileInspector.InspectPath(path);
                var length = new FileInfo(path).Length;
                aggregateBytes = checked(aggregateBytes + length);
                artifacts.Add(new TriggerQueueArtifactSnapshot(path, generation, identity, length, string.Empty));
                continue;
            }

            if (TryParseCleanupName(name, out generation, out var expectedIdentity))
            {
                var identity = TriggerQueueNativeFileInspector.InspectPath(path);
                if (identity != expectedIdentity)
                {
                    throw new InvalidOperationException("Trigger queue persistence found a substituted interrupted cleanup claim.");
                }

                var length = new FileInfo(path).Length;
                aggregateBytes = checked(aggregateBytes + length);
                cleanupArtifacts.Add(new TriggerQueueArtifactSnapshot(path, generation, identity, length, string.Empty));
                continue;
            }

            if (TryParseTombstoneName(name, out generation, out expectedIdentity))
            {
                if (tombstones.Count == _maxTombstoneArtifacts)
                {
                    throw new InvalidOperationException($"Trigger queue persistence refuses more than {_maxTombstoneArtifacts} authenticated tombstones.");
                }

                var identity = TriggerQueueNativeFileInspector.InspectPath(path);
                if (identity != expectedIdentity)
                {
                    throw new InvalidOperationException("Trigger queue persistence found a substituted authenticated tombstone.");
                }

                var length = new FileInfo(path).Length;
                aggregateBytes = checked(aggregateBytes + length);
                tombstones.Add(new TriggerQueueArtifactSnapshot(path, generation, identity, length, string.Empty));
                continue;
            }

            if (TryParseStagedName(name, out generation, out expectedIdentity)
                || TryParseDiscardName(name, out generation, out expectedIdentity))
            {
                var identity = TriggerQueueNativeFileInspector.InspectPath(path);
                if (identity != expectedIdentity)
                {
                    throw new InvalidOperationException("Trigger queue persistence found a substituted authenticated staging artifact.");
                }

                var length = new FileInfo(path).Length;
                aggregateBytes = checked(aggregateBytes + length);
                abandonedArtifacts.Add(new TriggerQueueArtifactSnapshot(path, generation, identity, length, string.Empty));
                continue;
            }

            if (TryParsePrecursorName(name, out generation))
            {
                var identity = TriggerQueueNativeFileInspector.InspectPath(path);
                var length = new FileInfo(path).Length;
                if (length != 0)
                {
                    throw new FormatException("Trigger queue persistence found a non-empty unauthenticated staging precursor and preserved it.");
                }

                if (precursorArtifacts.Count == _maxTombstoneArtifacts)
                {
                    throw new TriggerQueuePersistenceBackpressureException();
                }

                precursorArtifacts.Add(new TriggerQueueArtifactSnapshot(path, generation, identity, length, string.Empty));
                continue;
            }

            throw new FormatException($"Trigger queue persistence found an unsupported artifact: `{name}`.");
        }

        if (aggregateBytes > aggregateLimit)
        {
            throw new InvalidOperationException("Trigger queue persistence directory exceeds its bounded aggregate byte limit.");
        }

        observer.OnArtifactsObserved(_queueRoot);
        ValidateMutationLease(mutationLease);
        artifacts = artifacts.Select(artifact => BindArtifactContent(artifact, mutationLease.DirectoryAuthority)).ToList();
        cleanupArtifacts = cleanupArtifacts.Select(artifact => BindArtifactContent(artifact, mutationLease.DirectoryAuthority)).ToList();
        abandonedArtifacts = abandonedArtifacts.Select(artifact => BindArtifactContent(artifact, mutationLease.DirectoryAuthority)).ToList();
        tombstones = tombstones.Select(artifact => BindArtifactContent(artifact, mutationLease.DirectoryAuthority)).ToList();
        precursorArtifacts = precursorArtifacts.Select(artifact => BindArtifactContent(artifact, mutationLease.DirectoryAuthority)).ToList();

        if (precursorArtifacts.Count + tombstones.Count + abandonedArtifacts.Count > _maxTombstoneArtifacts)
        {
            throw new TriggerQueuePersistenceBackpressureException();
        }

        tombstones = tombstones
            .Select(artifact => CompleteInterruptedTombstone(artifact, mutationLease))
            .ToList();

        foreach (var abandoned in abandonedArtifacts)
        {
            var tombstone = ClaimAndReclaimExact(abandoned, recoverAsLedger: false, NullTriggerQueueDurabilityObserver.Instance, mutationLease);
            if (tombstone is not null)
            {
                tombstones.Add(tombstone);
            }
        }

        if (abandonedArtifacts.Count > 0)
        {
            mutationLease.DirectoryAuthority.Flush();
        }

        foreach (var cleanup in cleanupArtifacts)
        {
            if (artifacts.Any(artifact => artifact.Generation == cleanup.Generation))
            {
                throw new FormatException("Trigger queue persistence found both a ledger generation and interrupted cleanup claim for the same generation.");
            }

            if (artifacts.Count == MaxDiscoveredGenerations)
            {
                throw new InvalidOperationException($"Trigger queue persistence refuses more than {MaxDiscoveredGenerations} combined ledger and interrupted-cleanup generations.");
            }

            var restoredPath = GetPath($"ledger-{cleanup.Generation:D19}.json");
            MoveNoReplaceDurably(cleanup.Path, restoredPath, mutationLease);
            ValidateMutationLease(mutationLease);
            if (TriggerQueueNativeFileInspector.InspectPath(restoredPath) != cleanup.Identity)
            {
                throw new InvalidOperationException("Trigger queue interrupted cleanup restoration changed file identity.");
            }

            artifacts.Add(cleanup with { Path = restoredPath });
        }

        if (cleanupArtifacts.Count > 0)
        {
            mutationLease.DirectoryAuthority.Flush();
        }

        var ordered = artifacts.OrderBy(item => item.Generation).ToArray();
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index].Generation != checked(ordered[index - 1].Generation + 1))
            {
                throw new FormatException("Trigger queue persistence found a noncontiguous ledger generation sequence.");
            }
        }

        var latestContent = ordered.Length == 0
            ? null
            : await ReadExactAsync(ordered[^1], maximumBytes, mutationLease.DirectoryAuthority, cancellationToken).ConfigureAwait(false);

        ValidateMutationLease(mutationLease);
        return new TriggerQueueReadResult(ordered, latestContent, tombstones, precursorArtifacts);
    }

    /// <summary>Publishes a flushed immutable generation without replacing any path, then identity-cleans older generations.</summary>
    public async Task WriteAsync(
        byte[] content,
        IReadOnlyList<TriggerQueueArtifactSnapshot> previousArtifacts,
        IReadOnlyList<TriggerQueueArtifactSnapshot> tombstones,
        IReadOnlyList<TriggerQueueArtifactSnapshot> precursors,
        long generation,
        ITriggerQueueDurabilityObserver observer,
        TriggerQueueMutationLease mutationLease)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(previousArtifacts);
        ArgumentNullException.ThrowIfNull(tombstones);
        ArgumentNullException.ThrowIfNull(precursors);
        ArgumentNullException.ThrowIfNull(observer);
        ArgumentNullException.ThrowIfNull(mutationLease);
        ValidateMutationLease(mutationLease);
        ValidatePreservedTombstones(tombstones, mutationLease.DirectoryAuthority);
        ValidatePreservedPrecursors(precursors, mutationLease.DirectoryAuthority);
        var retiredArtifactSlots = tombstones.Count + Math.Max(0, previousArtifacts.Count - 1);
        var reservedArtifacts = precursors.Count + (OperatingSystem.IsWindows()
            ? 0
            : _recycleAuthenticatedTombstones
                ? Math.Max(1, retiredArtifactSlots)
                : tombstones.Count + Math.Max(1, previousArtifacts.Count));
        if (reservedArtifacts > _maxTombstoneArtifacts)
        {
            throw new TriggerQueuePersistenceBackpressureException();
        }

        var reusableTombstones = tombstones.ToList();
        foreach (var artifact in previousArtifacts.Take(Math.Max(0, previousArtifacts.Count - 1)))
        {
            var reclaimed = ClaimAndReclaimExact(artifact, recoverAsLedger: true, observer, mutationLease);
            if (reclaimed is not null)
            {
                reusableTombstones.Add(reclaimed);
            }
        }

        if (previousArtifacts.Count > 1)
        {
            mutationLease.DirectoryAuthority.Flush();
        }

        // Unix cannot unlink a path by the already-proven file handle. Reusing one authenticated,
        // zero-length cleanup inode as staging preserves exact identity without growing tombstones.
        var recycledTombstone = !OperatingSystem.IsWindows()
            && _recycleAuthenticatedTombstones
            && reusableTombstones.Count > 0
                ? reusableTombstones[0]
                : null;
        var retainedTombstones = recycledTombstone is null
            ? reusableTombstones.ToArray()
            : reusableTombstones.Skip(1).ToArray();
        ValidatePreservedTombstones(reusableTombstones, mutationLease.DirectoryAuthority);
        var destinationPath = GetPath($"ledger-{generation:D19}.json");
        var tempPath = recycledTombstone?.Path
            ?? GetPath($".ledger-{generation:D19}.{Guid.NewGuid():N}.tmp");
        TriggerQueueFileIdentity? stagedIdentity = null;
        TriggerQueueArtifactSnapshot? publishedArtifact = null;
        try
        {
            observer.OnStagingDirectoryBound(generation, tempPath, destinationPath);
            ValidateMutationLease(mutationLease);
            ValidatePreservedPrecursors(precursors, mutationLease.DirectoryAuthority);
            ValidatePreservedTombstones(reusableTombstones, mutationLease.DirectoryAuthority);
            FileStream stream;
            if (recycledTombstone is null)
            {
                // Windows needs delete sharing for the immediate handle-retaining rename. Every later boundary
                // re-proves that the owned path still resolves to this exact single-link regular-file identity.
                stream = mutationLease.DirectoryAuthority.CreateNew(
                    Path.GetFileName(tempPath),
                    FileAccess.Write,
                    FileShare.Read | FileShare.Delete,
                    4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
                stagedIdentity = TriggerQueueNativeFileInspector.InspectHandle(stream.SafeFileHandle, tempPath);
                if (stagedIdentity != TriggerQueueNativeFileInspector.InspectPath(tempPath))
                {
                    stream.Dispose();
                    throw new InvalidOperationException("Trigger queue staging path was substituted while opening it.");
                }
            }
            else
            {
                stream = mutationLease.DirectoryAuthority.OpenExisting(
                    Path.GetFileName(tempPath),
                    FileAccess.ReadWrite,
                    FileShare.Read | FileShare.Delete,
                    4096);
                try
                {
                    ValidateArtifactHandleContent(stream, recycledTombstone);
                    stagedIdentity = TriggerQueueNativeFileInspector.InspectHandle(stream.SafeFileHandle, tempPath);
                    if (stagedIdentity != recycledTombstone.Identity
                        || stagedIdentity != TriggerQueueNativeFileInspector.InspectPath(tempPath))
                    {
                        throw new InvalidOperationException("Trigger queue recycled tombstone changed while opening it for staging.");
                    }
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }

            await using (stream)
            {
                observer.OnStagingPrecursorCreated(generation, tempPath, destinationPath);
                ValidateMutationLease(mutationLease);
                ValidatePreservedTombstones(reusableTombstones, mutationLease.DirectoryAuthority);
                if (stream.Length != 0 || TriggerQueueNativeFileInspector.InspectPath(tempPath) != stagedIdentity)
                {
                    throw new InvalidOperationException("Trigger queue empty staging precursor changed before identity-bound naming.");
                }

                var ownedTempPath = GetPath($".staged-{generation:D19}-{stagedIdentity.Device:X16}-{stagedIdentity.File:X16}.{Guid.NewGuid():N}.tmp");
                MoveNoReplaceDurably(tempPath, ownedTempPath, mutationLease);
                if (TriggerQueueNativeFileInspector.InspectPath(ownedTempPath) != stagedIdentity)
                {
                    if (!File.Exists(tempPath))
                    {
                        MoveNoReplaceDurably(ownedTempPath, tempPath, mutationLease);
                    }

                    throw new InvalidOperationException("Trigger queue staging identity changed during identity-bound naming.");
                }

                tempPath = ownedTempPath;
                ValidateMutationLease(mutationLease);
                ValidatePreservedTombstones(retainedTombstones, mutationLease.DirectoryAuthority);

                await stream.WriteAsync(content, CancellationToken.None).ConfigureAwait(false);
                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (TriggerQueueNativeFileInspector.InspectPath(tempPath) != stagedIdentity)
            {
                throw new InvalidOperationException("Trigger queue staging path changed after content flush.");
            }

            ValidateMutationLease(mutationLease);
            observer.OnStaged(generation, tempPath, destinationPath);
            ValidateMutationLease(mutationLease);
            ValidatePreservedTombstones(retainedTombstones, mutationLease.DirectoryAuthority);
            if (TriggerQueueNativeFileInspector.InspectPath(tempPath) != stagedIdentity)
            {
                throw new InvalidOperationException("Trigger queue staging path was substituted before publication.");
            }

            observer.OnPublishing(generation, tempPath, destinationPath);
            ValidateMutationLease(mutationLease);
            ValidatePreservedPrecursors(precursors, mutationLease.DirectoryAuthority);
            ValidatePreservedTombstones(retainedTombstones, mutationLease.DirectoryAuthority);
            if (TriggerQueueNativeFileInspector.InspectPath(tempPath) != stagedIdentity)
            {
                throw new InvalidOperationException("Trigger queue staging path was substituted at the publication boundary.");
            }

            MoveNoReplaceDurably(tempPath, destinationPath, mutationLease, () => observer.OnPublishingDirectoryBound(generation, tempPath, destinationPath));
            ValidateMutationLease(mutationLease);
            publishedArtifact = new TriggerQueueArtifactSnapshot(destinationPath, generation, stagedIdentity, content.Length, Hash(content));
            ValidateArtifactContent(publishedArtifact, mutationLease.DirectoryAuthority);
            if (TriggerQueueNativeFileInspector.InspectPath(destinationPath) != stagedIdentity)
            {
                throw new InvalidOperationException("Trigger queue publication identity did not match the flushed staging file.");
            }

            mutationLease.DirectoryAuthority.Flush();
            observer.OnPublished(generation, destinationPath);
            ValidateMutationLease(mutationLease);
            ValidatePreservedPrecursors(precursors, mutationLease.DirectoryAuthority);
            ValidatePreservedTombstones(retainedTombstones, mutationLease.DirectoryAuthority);
            ValidateArtifactContent(publishedArtifact, mutationLease.DirectoryAuthority);
            if (TriggerQueueNativeFileInspector.InspectPath(destinationPath) != stagedIdentity)
            {
                throw new InvalidOperationException("Trigger queue published generation was substituted before prior evidence cleanup.");
            }

            foreach (var artifact in previousArtifacts)
            {
                ClaimAndReclaimExact(artifact, recoverAsLedger: true, observer, mutationLease);
            }

            mutationLease.DirectoryAuthority.Flush();
            ValidatePreservedPrecursors(precursors, mutationLease.DirectoryAuthority);
            ValidatePreservedTombstones(retainedTombstones, mutationLease.DirectoryAuthority);
            ValidateArtifactContent(publishedArtifact, mutationLease.DirectoryAuthority);
            if (TriggerQueueNativeFileInspector.InspectPath(destinationPath) != stagedIdentity)
            {
                throw new InvalidOperationException("Trigger queue published generation was substituted before commit completion.");
            }
        }
        finally
        {
            if (stagedIdentity is not null)
            {
                var exists = true;
                try
                {
                    TriggerQueueNativeFileInspector.InspectPath(tempPath);
                }
                catch (FileNotFoundException)
                {
                    exists = false;
                }
                catch (Win32Exception exception) when (exception.NativeErrorCode == 2)
                {
                    exists = false;
                }

                if (exists)
                {
                    var observed = new TriggerQueueArtifactSnapshot(tempPath, generation, stagedIdentity, new FileInfo(tempPath).Length, string.Empty);
                    ClaimAndReclaimExact(BindArtifactContent(observed, mutationLease.DirectoryAuthority), recoverAsLedger: false, observer, mutationLease);
                }
            }
        }
    }

    private async Task<byte[]> ReadExactAsync(TriggerQueueArtifactSnapshot artifact, int maximumBytes, TriggerQueueDirectoryAuthority directoryAuthority, CancellationToken cancellationToken)
    {
        await using var stream = OpenArtifact(artifact, directoryAuthority);
        var handleIdentity = TriggerQueueNativeFileInspector.InspectHandle(stream.SafeFileHandle, artifact.Path);
        if (artifact.Identity != handleIdentity || stream.Length != artifact.Length || stream.Length > maximumBytes)
        {
            throw new InvalidOperationException("Trigger queue ledger was substituted or exceeded its configured byte bound.");
        }

        using var content = new MemoryStream(checked((int)stream.Length));
        var buffer = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (content.Length + read > maximumBytes)
            {
                throw new InvalidOperationException("Trigger queue ledger exceeded its configured byte bound while reading.");
            }

            content.Write(buffer, 0, read);
        }

        if (TriggerQueueNativeFileInspector.InspectPath(artifact.Path) != handleIdentity)
        {
            throw new InvalidOperationException("Trigger queue ledger path was substituted while reading.");
        }

        var result = content.ToArray();
        if (!string.Equals(Hash(result), artifact.ContentHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Trigger queue ledger content changed after artifact discovery.");
        }

        return result;
    }

    private TriggerQueueArtifactSnapshot? ClaimAndReclaimExact(
        TriggerQueueArtifactSnapshot artifact,
        bool recoverAsLedger,
        ITriggerQueueDurabilityObserver observer,
        TriggerQueueMutationLease mutationLease)
    {
        ValidateMutationLease(mutationLease);
        if (!File.Exists(artifact.Path))
        {
            return null;
        }

        if (TriggerQueueNativeFileInspector.InspectPath(artifact.Path) != artifact.Identity)
        {
            throw new InvalidOperationException("Trigger queue generation cleanup refused to claim a substituted file.");
        }

        ValidateArtifactContent(artifact, mutationLease.DirectoryAuthority);

        var prefix = recoverAsLedger ? ".cleanup" : ".discard";
        var claimPath = GetPath($"{prefix}-{artifact.Generation:D19}-{artifact.Identity.Device:X16}-{artifact.Identity.File:X16}.{Guid.NewGuid():N}.tmp");
        observer.OnCleanupPrepared(artifact.Generation, artifact.Path, claimPath);
        ValidateMutationLease(mutationLease);
        MoveNoReplaceDurably(artifact.Path, claimPath, mutationLease);
        ValidateMutationLease(mutationLease);
        var claimed = artifact with { Path = claimPath };
        if (TriggerQueueNativeFileInspector.InspectPath(claimPath) != artifact.Identity)
        {
            if (!File.Exists(artifact.Path))
            {
                MoveNoReplaceDurably(claimPath, artifact.Path, mutationLease);
            }

            throw new InvalidOperationException("Trigger queue generation cleanup claimed a substituted file and preserved it.");
        }


        ValidateArtifactContent(claimed, mutationLease.DirectoryAuthority);

        observer.OnCleanupClaimed(artifact.Generation, claimPath);
        ValidateMutationLease(mutationLease);
        if (TriggerQueueNativeFileInspector.InspectPath(claimPath) != artifact.Identity)
        {
            throw new InvalidOperationException("Trigger queue generation cleanup refused to delete a substituted cleanup claim.");
        }


        ValidateArtifactContent(claimed, mutationLease.DirectoryAuthority);

        observer.OnCleanupDeleting(artifact.Generation, claimPath);
        ValidateMutationLease(mutationLease);
        if (TriggerQueueNativeFileInspector.InspectPath(claimPath) != artifact.Identity)
        {
            throw new InvalidOperationException("Trigger queue generation cleanup refused a last-window claim substitution.");
        }

        if (OperatingSystem.IsWindows())
        {
            DeleteExactOnWindows(claimed);
            return null;
        }

        return TombstoneExactOnUnix(claimed, mutationLease);
    }

    private TriggerQueueArtifactSnapshot TombstoneExactOnUnix(
        TriggerQueueArtifactSnapshot artifact,
        TriggerQueueMutationLease mutationLease)
    {
        using var stream = mutationLease.DirectoryAuthority.OpenExisting(Path.GetFileName(artifact.Path), FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 4096);
        ValidateArtifactHandleContent(stream, artifact);
        if (TriggerQueueNativeFileInspector.InspectPath(artifact.Path) != artifact.Identity)
        {
            throw new InvalidOperationException("Trigger queue cleanup claim was substituted before handle-bound tombstoning.");
        }

        var tombstonePath = GetPath($".tombstone-{artifact.Generation:D19}-{artifact.Identity.Device:X16}-{artifact.Identity.File:X16}.{Guid.NewGuid():N}.tmp");
        MoveNoReplaceDurably(artifact.Path, tombstonePath, mutationLease);
        if (TriggerQueueNativeFileInspector.InspectPath(tombstonePath) != artifact.Identity)
        {
            throw new InvalidOperationException("Trigger queue cleanup tombstone did not retain the exact proven file identity.");
        }

        stream.SetLength(0);
        stream.Flush(flushToDisk: true);
        if (TriggerQueueNativeFileInspector.InspectHandle(stream.SafeFileHandle, tombstonePath) != artifact.Identity
            || TriggerQueueNativeFileInspector.InspectPath(tombstonePath) != artifact.Identity
            || stream.Length != 0)
        {
            throw new InvalidOperationException("Trigger queue cleanup handle changed while creating its zero-length tombstone.");
        }

        return new TriggerQueueArtifactSnapshot(
            tombstonePath,
            artifact.Generation,
            artifact.Identity,
            0,
            Hash(ReadOnlySpan<byte>.Empty));
    }

    private static void DeleteExactOnWindows(TriggerQueueArtifactSnapshot artifact)
    {
        const uint Delete = 0x00010000;
        const uint GenericRead = 0x80000000;
        const uint FileShareRead = 0x00000001;
        const uint FileShareWrite = 0x00000002;
        const uint FileShareDelete = 0x00000004;
        const uint OpenExisting = 3;
        const uint FileFlagOpenReparsePoint = 0x00200000;
        using var handle = CreateFile(artifact.Path, Delete | GenericRead, FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero, OpenExisting, FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Trigger queue cleanup claim could not be opened for handle-bound deletion.");
        }

        using var stream = new FileStream(handle, FileAccess.Read);
        ValidateArtifactHandleContent(stream, artifact);
        if (TriggerQueueNativeFileInspector.InspectPath(artifact.Path) != artifact.Identity)
        {
            throw new InvalidOperationException("Trigger queue cleanup claim was substituted before handle-bound deletion.");
        }

        var disposition = new TriggerQueueFileDispositionInformation { DeleteFile = true };
        const int FileDispositionInfo = 4;
        if (!SetFileInformationByHandle(handle, FileDispositionInfo, ref disposition, (uint)Marshal.SizeOf<TriggerQueueFileDispositionInformation>()))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Trigger queue handle-bound cleanup deletion failed.");
        }
    }

    private static void ValidateArtifactHandleContent(FileStream stream, TriggerQueueArtifactSnapshot artifact)
    {
        var identity = TriggerQueueNativeFileInspector.InspectHandle(stream.SafeFileHandle, artifact.Path);
        if (identity != artifact.Identity || stream.Length != artifact.Length)
        {
            throw new InvalidOperationException("Trigger queue cleanup handle did not match its authenticated artifact evidence.");
        }

        stream.Position = 0;
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(hash, artifact.ContentHash, StringComparison.Ordinal) || stream.Length != artifact.Length)
        {
            throw new InvalidOperationException("Trigger queue cleanup handle content changed after authentication.");
        }
    }

    private static TriggerQueueArtifactSnapshot BindArtifactContent(TriggerQueueArtifactSnapshot artifact, TriggerQueueDirectoryAuthority directoryAuthority)
    {
        using var stream = OpenArtifact(artifact, directoryAuthority);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (TriggerQueueNativeFileInspector.InspectPath(artifact.Path) != artifact.Identity || stream.Length != artifact.Length)
        {
            throw new InvalidOperationException("Trigger queue artifact changed while binding its cleanup evidence.");
        }

        return artifact with { ContentHash = hash };
    }

    private static void ValidateArtifactContent(TriggerQueueArtifactSnapshot artifact, TriggerQueueDirectoryAuthority directoryAuthority)
    {
        using var stream = OpenArtifact(artifact, directoryAuthority);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(hash, artifact.ContentHash, StringComparison.Ordinal)
            || TriggerQueueNativeFileInspector.InspectPath(artifact.Path) != artifact.Identity
            || stream.Length != artifact.Length)
        {
            throw new InvalidOperationException("Trigger queue cleanup refused artifact content that changed after observation.");
        }
    }

    private static void ValidatePreservedPrecursors(IReadOnlyList<TriggerQueueArtifactSnapshot> precursors, TriggerQueueDirectoryAuthority directoryAuthority)
    {
        foreach (var precursor in precursors)
        {
            using var stream = directoryAuthority.OpenExisting(Path.GetFileName(precursor.Path), FileAccess.Read, FileShare.Read, 4096);
            ValidateArtifactHandleContent(stream, precursor);
            if (TriggerQueueNativeFileInspector.InspectPath(precursor.Path) != precursor.Identity)
            {
                throw new InvalidOperationException("Trigger queue preserved precursor path changed after handle-bound validation.");
            }
        }
    }

    private static void ValidatePreservedTombstones(
        IReadOnlyList<TriggerQueueArtifactSnapshot> tombstones,
        TriggerQueueDirectoryAuthority directoryAuthority)
    {
        foreach (var tombstone in tombstones)
        {
            if (!TryParseTombstoneName(Path.GetFileName(tombstone.Path), out var generation, out var identity)
                || generation != tombstone.Generation
                || identity != tombstone.Identity
                || tombstone.Length != 0)
            {
                throw new InvalidOperationException("Trigger queue preserved tombstone evidence is malformed.");
            }

            using var stream = directoryAuthority.OpenExisting(
                Path.GetFileName(tombstone.Path),
                FileAccess.Read,
                FileShare.Read,
                4096);
            ValidateArtifactHandleContent(stream, tombstone);
            if (TriggerQueueNativeFileInspector.InspectPath(tombstone.Path) != tombstone.Identity)
            {
                throw new InvalidOperationException("Trigger queue preserved tombstone path changed after handle-bound validation.");
            }
        }
    }

    private static TriggerQueueArtifactSnapshot CompleteInterruptedTombstone(
        TriggerQueueArtifactSnapshot tombstone,
        TriggerQueueMutationLease mutationLease)
    {
        if (tombstone.Length == 0)
        {
            return tombstone;
        }

        using var stream = mutationLease.DirectoryAuthority.OpenExisting(
            Path.GetFileName(tombstone.Path),
            FileAccess.ReadWrite,
            FileShare.Read | FileShare.Delete,
            4096);
        ValidateArtifactHandleContent(stream, tombstone);
        if (TriggerQueueNativeFileInspector.InspectPath(tombstone.Path) != tombstone.Identity)
        {
            throw new InvalidOperationException("Trigger queue interrupted tombstone changed before handle-bound completion.");
        }

        stream.SetLength(0);
        stream.Flush(flushToDisk: true);
        if (TriggerQueueNativeFileInspector.InspectHandle(stream.SafeFileHandle, tombstone.Path) != tombstone.Identity
            || TriggerQueueNativeFileInspector.InspectPath(tombstone.Path) != tombstone.Identity
            || stream.Length != 0)
        {
            throw new InvalidOperationException("Trigger queue interrupted tombstone changed during handle-bound completion.");
        }

        return tombstone with { Length = 0, ContentHash = Hash(ReadOnlySpan<byte>.Empty) };
    }

    private static FileStream OpenArtifact(TriggerQueueArtifactSnapshot artifact, TriggerQueueDirectoryAuthority directoryAuthority)
    {
        FileStream stream;
        try
        {
            stream = directoryAuthority.OpenExisting(Path.GetFileName(artifact.Path), FileAccess.Read, FileShare.Read, 4096);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException("Trigger queue artifact became unavailable or unsafe after observation.", exception);
        }

        if (TriggerQueueNativeFileInspector.InspectHandle(stream.SafeFileHandle, artifact.Path) != artifact.Identity || stream.Length != artifact.Length)
        {
            stream.Dispose();
            throw new InvalidOperationException("Trigger queue artifact identity or length changed after observation.");
        }

        return stream;
    }

    private static string Hash(ReadOnlySpan<byte> content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static bool TryParseLedgerName(string name, out long generation)
    {
        generation = 0;
        return name.Length == 31
            && name.StartsWith("ledger-", StringComparison.Ordinal)
            && name.EndsWith(".json", StringComparison.Ordinal)
            && long.TryParse(name.AsSpan(7, 19), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out generation)
            && generation >= 1;
    }

    private static bool TryParsePrecursorName(string name, out long generation)
    {
        generation = 0;
        return name.Length == 64
            && name.StartsWith(".ledger-", StringComparison.Ordinal)
            && name.EndsWith(".tmp", StringComparison.Ordinal)
            && name[27] == '.'
            && long.TryParse(name.AsSpan(8, 19), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out generation)
            && generation >= 1
            && Guid.TryParseExact(name.AsSpan(28, 32), "N", out _);
    }

    private static bool TryParseCleanupName(string name, out long generation, out TriggerQueueFileIdentity identity)
    {
        return TryParseClaimName(name, ".cleanup-", out generation, out identity);
    }

    private static bool TryParseTombstoneName(string name, out long generation, out TriggerQueueFileIdentity identity)
    {
        return TryParseClaimName(name, ".tombstone-", out generation, out identity);
    }

    private static bool TryParseStagedName(string name, out long generation, out TriggerQueueFileIdentity identity)
    {
        return TryParseClaimName(name, ".staged-", out generation, out identity);
    }

    private static bool TryParseDiscardName(string name, out long generation, out TriggerQueueFileIdentity identity)
    {
        return TryParseClaimName(name, ".discard-", out generation, out identity);
    }

    private static bool TryParseClaimName(string name, string prefix, out long generation, out TriggerQueueFileIdentity identity)
    {
        generation = 0;
        identity = new TriggerQueueFileIdentity(0, 0, 0);
        var numberStyles = System.Globalization.NumberStyles.None;
        var hexStyles = System.Globalization.NumberStyles.AllowHexSpecifier;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        return name.Length == prefix.Length + 90
            && name.StartsWith(prefix, StringComparison.Ordinal)
            && name.EndsWith(".tmp", StringComparison.Ordinal)
            && name[prefix.Length + 19] == '-'
            && name[prefix.Length + 36] == '-'
            && name[prefix.Length + 53] == '.'
            && long.TryParse(name.AsSpan(prefix.Length, 19), numberStyles, culture, out generation)
            && generation >= 1
            && ulong.TryParse(name.AsSpan(prefix.Length + 20, 16), hexStyles, culture, out var device)
            && ulong.TryParse(name.AsSpan(prefix.Length + 37, 16), hexStyles, culture, out var file)
            && Guid.TryParseExact(name.AsSpan(prefix.Length + 54, 32), "N", out _)
            && (identity = new TriggerQueueFileIdentity(device, file, 1)) is var _;
    }

    private void PrepareRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            PrepareRootUnix();
            return;
        }

        EnsureNoReparsePoints(_queueRoot);
        var missing = new List<string>();
        var current = _queueRoot;
        while (!Directory.Exists(current))
        {
            missing.Add(current);
            current = Path.GetDirectoryName(current) ?? throw new InvalidOperationException("Trigger queue root does not have a containing directory.");
            EnsureContained(_workspaceRoot, current);
        }

        for (var index = missing.Count - 1; index >= 0; index--)
        {
            var path = missing[index];
            var parent = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Trigger queue directory does not have a containing directory.");
            var temporary = Path.Combine(parent, $".trigger-queue-directory-{Guid.NewGuid():N}.tmp");
            Directory.CreateDirectory(temporary);
            try
            {
                MoveNoReplaceDurably(temporary, path, null);
            }
            catch (IOException exception) when (IsCompetingDirectoryCreation(exception, path))
            {
                EnsureNoReparsePoints(path);
                Directory.Delete(temporary);
            }

            EnsureNoReparsePoints(path);
        }

        EnsureNoReparsePoints(_queueRoot);
    }

    [ExcludeFromCodeCoverage(Justification = "This descriptor-relative root-creation path is covered by public trigger-queue behavior on Linux/macOS and is unreachable in Windows coverage runs.")]
    private void PrepareRootUnix()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("Descriptor-relative trigger queue directory creation is not available on this platform.");
        }

        EnsureContained(_workspaceRoot, _queueRoot);
        var directoryFlag = OperatingSystem.IsMacOS() ? 0x100000 : 0x10000;
        var closeOnExecFlag = OperatingSystem.IsMacOS() ? 0x1000000 : 0x80000;
        var noFollowFlag = OperatingSystem.IsMacOS() ? 0x100 : 0x20000;
        var openFlags = directoryFlag | closeOnExecFlag | noFollowFlag;
        var descriptor = Open(_workspaceRoot, openFlags);
        if (descriptor < 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Trigger queue workspace root could not be pinned for directory creation.");
        }

        SafeFileHandle? currentHandle = new(new IntPtr(descriptor), ownsHandle: true);
        try
        {
            var expectedWorkspaceIdentity = TriggerQueueNativeFileInspector.InspectDirectoryPath(_workspaceRoot);
            var pinnedWorkspaceIdentity = TriggerQueueNativeFileInspector.InspectDirectoryHandle(currentHandle, _workspaceRoot);
            if (pinnedWorkspaceIdentity.Device != expectedWorkspaceIdentity.Device || pinnedWorkspaceIdentity.File != expectedWorkspaceIdentity.File)
            {
                throw new InvalidOperationException("Trigger queue workspace root changed while binding descriptor-relative directory authority.");
            }

            var relative = Path.GetRelativePath(_workspaceRoot, _queueRoot);
            var currentPath = _workspaceRoot;
            foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment is "." or "..")
                {
                    throw new InvalidOperationException("Trigger queue root contains an invalid directory segment.");
                }

                var parentDescriptor = currentHandle.DangerousGetHandle().ToInt32();
                var childDescriptor = OpenAt(parentDescriptor, segment, openFlags);
                if (childDescriptor < 0)
                {
                    const int NoSuchFileOrDirectory = 2;
                    const int AlreadyExists = 17;
                    var openError = Marshal.GetLastWin32Error();
                    if (openError != NoSuchFileOrDirectory)
                    {
                        throw new Win32Exception(openError, "Trigger queue directory segment could not be opened without following links.");
                    }

                    if (MkdirAt(parentDescriptor, segment, 0x1FF) != 0)
                    {
                        var createError = Marshal.GetLastWin32Error();
                        if (createError != AlreadyExists)
                        {
                            throw new Win32Exception(createError, "Trigger queue directory segment could not be created through pinned parent authority.");
                        }
                    }
                    else if (Fsync(parentDescriptor) != 0)
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Trigger queue parent directory durability flush failed after directory creation.");
                    }

                    childDescriptor = OpenAt(parentDescriptor, segment, openFlags);
                    if (childDescriptor < 0)
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Trigger queue directory segment could not be pinned after bounded creation race.");
                    }
                }

                var childHandle = new SafeFileHandle(new IntPtr(childDescriptor), ownsHandle: true);
                currentPath = Path.Combine(currentPath, segment);
                try
                {
                    var pinnedChildIdentity = TriggerQueueNativeFileInspector.InspectDirectoryHandle(childHandle, currentPath);
                    var pathChildIdentity = TriggerQueueNativeFileInspector.InspectDirectoryPath(currentPath);
                    if (pinnedChildIdentity.Device != pathChildIdentity.Device || pinnedChildIdentity.File != pathChildIdentity.File)
                    {
                        throw new InvalidOperationException("Trigger queue directory chain changed during descriptor-relative creation.");
                    }
                }
                catch
                {
                    childHandle.Dispose();
                    throw;
                }

                currentHandle.Dispose();
                currentHandle = childHandle;
            }
        }
        finally
        {
            currentHandle?.Dispose();
        }

        EnsureNoReparsePoints(_queueRoot);
    }

    private static bool IsCompetingDirectoryCreation(IOException exception, string path)
    {
        const int ErrorAccessDenied = 5;
        const int ErrorAlreadyExists = 183;
        return exception.InnerException is Win32Exception { NativeErrorCode: ErrorAccessDenied or ErrorAlreadyExists } && Directory.Exists(path);
    }

    private IReadOnlyList<TriggerQueueDirectorySnapshot> CaptureRootSnapshot()
    {
        EnsureNoReparsePoints(_queueRoot);
        var snapshots = new List<TriggerQueueDirectorySnapshot>();
        var relative = Path.GetRelativePath(_workspaceRoot, _queueRoot);
        var current = _workspaceRoot;
        snapshots.Add(new TriggerQueueDirectorySnapshot(current, TriggerQueueNativeFileInspector.InspectDirectoryPath(current)));
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            snapshots.Add(new TriggerQueueDirectorySnapshot(current, TriggerQueueNativeFileInspector.InspectDirectoryPath(current)));
        }

        return snapshots;
    }

    private void ValidateRootSnapshot(IReadOnlyList<TriggerQueueDirectorySnapshot> rootSnapshot)
    {
        EnsureNoReparsePoints(_queueRoot);
        foreach (var snapshot in rootSnapshot)
        {
            var current = TriggerQueueNativeFileInspector.InspectDirectoryPath(snapshot.Path);
            if (current.Device != snapshot.Identity.Device || current.File != snapshot.Identity.File)
            {
                throw new InvalidOperationException("Trigger queue persistence detected replacement of its governed directory chain.");
            }
        }
    }

    private void ValidateMutationLease(TriggerQueueMutationLease mutationLease)
    {
        mutationLease.DirectoryAuthority.ValidateCurrentPath();
        ValidateRootSnapshot(mutationLease.RootSnapshot);
        if (TriggerQueueNativeFileInspector.InspectPath(mutationLease.LockPath) != mutationLease.LockIdentity)
        {
            throw new InvalidOperationException("Trigger queue persistence detected replacement of its active cross-process lock path.");
        }
    }

    private void MoveNoReplaceDurably(string sourcePath, string destinationPath, TriggerQueueMutationLease? mutationLease, Action? onDirectoryBound = null)
    {
        var sourceDirectory = Path.GetFullPath(Path.GetDirectoryName(sourcePath) ?? throw new InvalidOperationException("Trigger queue source does not have a containing directory."));
        var destinationDirectory = Path.GetFullPath(Path.GetDirectoryName(destinationPath) ?? throw new InvalidOperationException("Trigger queue destination does not have a containing directory."));
        if (!string.Equals(sourceDirectory, destinationDirectory, _comparison))
        {
            throw new InvalidOperationException("Trigger queue no-replace publication requires one pinned containing directory.");
        }

        if (mutationLease is null)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new InvalidOperationException("Unix trigger queue publication requires captured mutation-lease directory authority.");
            }

            MoveNoReplaceWindows(sourcePath, destinationPath);
            return;
        }

        var authoritativeRoot = mutationLease.RootSnapshot[^1];
        if (!string.Equals(sourceDirectory, authoritativeRoot.Path, _comparison))
        {
            throw new InvalidOperationException("Trigger queue no-replace publication escaped the mutation lease's authoritative queue root.");
        }

        onDirectoryBound?.Invoke();
        mutationLease.DirectoryAuthority.MoveNoReplace(Path.GetFileName(sourcePath), Path.GetFileName(destinationPath));
    }

    private static void MoveNoReplaceWindows(string sourcePath, string destinationPath)
    {
        const uint MoveFileWriteThrough = 0x00000008;
        if (!MoveFileEx(sourcePath, destinationPath, MoveFileWriteThrough))
        {
            var error = Marshal.GetLastWin32Error();
            throw new IOException("Trigger queue durable no-replace rename failed.", new Win32Exception(error));
        }
    }

    private void EnsureNoReparsePoints(string target)
    {
        var safeTarget = Path.GetFullPath(target);
        EnsureContained(_workspaceRoot, safeTarget);
        ThrowIfReparsePoint(_workspaceRoot);
        var relative = Path.GetRelativePath(_workspaceRoot, safeTarget);
        var current = _workspaceRoot;
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            ThrowIfReparsePoint(current);
        }
    }

    private void EnsureContained(string root, string candidate)
    {
        if (string.Equals(root, candidate, _comparison))
        {
            return;
        }

        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, _comparison))
        {
            throw new InvalidOperationException("Trigger queue artifact path escaped its configured workspace root.");
        }
    }

    private static void ThrowIfReparsePoint(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException($"Trigger queue persistence refuses a reparse point: `{path}`.");
            }
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(int directoryDescriptor, string path, int flags);

    [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)]
    private static extern int MkdirAt(int directoryDescriptor, string path, uint mode);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int descriptor);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string existingFileName, string newFileName, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int fileInformationClass, ref TriggerQueueFileDispositionInformation fileInformation, uint bufferSize);
}
