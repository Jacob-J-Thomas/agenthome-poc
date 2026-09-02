using System.Text;
using EmbodySense.Core.Persistence.Capabilities.Models;
using Microsoft.Win32.SafeHandles;

namespace EmbodySense.Core.Persistence.Capabilities;

/// <summary>Holds a stable no-follow root and an exclusive catalog lock for one bounded filesystem transaction.</summary>
internal sealed class CapabilityCatalogPathSession : IAsyncDisposable, IDisposable
{
    private readonly string _root;
    private readonly StringComparison _comparison;
    private readonly Dictionary<string, SafeFileHandle> _directories;
    private readonly List<SafeFileHandle> _ownedDirectories;
    private readonly ICapabilityCatalogDurabilityBarrier _durabilityBarrier;
    private readonly ICapabilityCatalogPathObserver? _pathObserver;
    private readonly TimeProvider _timeProvider;
    private readonly string _pathBindingIdentityMaterial;
    private string? _physicalIdentityMaterial;
    private FileStream? _lock;
    private string? _lockPath;
    private string? _lockBindingIdentityMaterial;

    private CapabilityCatalogPathSession(string root, StringComparison comparison, Dictionary<string, SafeFileHandle> directories, List<SafeFileHandle> ownedDirectories, ICapabilityCatalogDurabilityBarrier durabilityBarrier, ICapabilityCatalogPathObserver? pathObserver, TimeProvider timeProvider)
    {
        _root = root;
        _comparison = comparison;
        _directories = directories;
        _ownedDirectories = ownedDirectories;
        _durabilityBarrier = durabilityBarrier;
        _pathObserver = pathObserver;
        _timeProvider = timeProvider;
        _pathBindingIdentityMaterial = CapabilityCatalogNativeFileSystem.GetPathBindingIdentityMaterial(directories[string.Empty]);
    }

    public string PhysicalIdentityMaterial => _physicalIdentityMaterial ??= CapabilityCatalogNativeFileSystem.GetPhysicalIdentityMaterial(_directories[string.Empty]);

    internal string PathBindingIdentityMaterial => _pathBindingIdentityMaterial;

    public string Root => _root;

    public static CapabilityCatalogPathSession? Open(string root, StringComparison comparison, bool createRoot, ICapabilityCatalogDurabilityBarrier? durabilityBarrier = null, ICapabilityCatalogPathObserver? pathObserver = null, TimeProvider? timeProvider = null)
    {
        durabilityBarrier ??= NativeCapabilityCatalogDurabilityBarrier.Instance;
        timeProvider ??= TimeProvider.System;
        var directories = new Dictionary<string, SafeFileHandle>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var owned = new List<SafeFileHandle>();
        try
        {
            var pathRoot = Path.GetPathRoot(root) ?? throw new IOException("Capability catalog persistence root has no filesystem root.");
            var currentPath = pathRoot;
            var current = CapabilityCatalogNativeFileSystem.OpenDirectory(currentPath, parent: null, name: null, create: false, durabilityBarrier, out _) ?? throw new IOException("Capability catalog filesystem root is unavailable.");
            owned.Add(current);
            var relative = Path.GetRelativePath(pathRoot, root);
            var keySegments = new List<string>();
            foreach (var segment in Split(relative))
            {
                currentPath = Path.Combine(currentPath, segment);
                var parent = current;
                pathObserver?.BeforeDirectoryChildOpen(Path.GetDirectoryName(currentPath)!, segment);
                var next = CapabilityCatalogNativeFileSystem.OpenDirectory(currentPath, parent, segment, createRoot, durabilityBarrier, out var created);
                if (next is null)
                {
                    foreach (var handle in owned)
                    {
                        handle.Dispose();
                    }
                    return null;
                }

                owned.Add(next);
                if (created)
                {
                    durabilityBarrier.FlushAfterDirectoryCreate(currentPath, parent);
                }
                current = next;
                keySegments.Add(segment);
            }

            directories[string.Empty] = current;
            return new CapabilityCatalogPathSession(root, comparison, directories, owned, durabilityBarrier, pathObserver, timeProvider);
        }
        catch
        {
            foreach (var handle in owned)
            {
                handle.Dispose();
            }
            throw;
        }
    }

    public async Task<bool> TryAcquireLockAsync(
        string path,
        bool createParent,
        CancellationToken cancellationToken,
        bool throwOnContentionTimeout = true,
        bool retryInitializationRaces = false)
    {
        var safePath = RequireContained(path);
        var parentPath = Path.GetDirectoryName(safePath) ?? throw new IOException("Capability catalog lock has no parent directory.");
        var parent = GetDirectory(parentPath, create: createParent);
        if (parent is null)
        {
            return false;
        }
        var name = Path.GetFileName(safePath);
        var initializationRaceAttempts = 0;
        for (var attempt = 0; attempt < 250; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _lock = CapabilityCatalogNativeFileSystem.TryAcquireExclusiveLock(safePath, parent, name);
            }
            catch (IOException exception) when (retryInitializationRaces && IsTransientInitializationRace(exception) && initializationRaceAttempts++ < 3)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), _timeProvider, cancellationToken);
                continue;
            }
            if (_lock is not null)
            {
                _lockPath = safePath;
                try
                {
                    _lockBindingIdentityMaterial = OperatingSystem.IsWindows()
                        ? null
                        : CapabilityCatalogNativeFileSystem.GetPathBindingIdentityMaterial(_lock.SafeFileHandle);
                    EnsureLockBinding();
                    return true;
                }
                catch
                {
                    ReleaseLock();
                    throw;
                }
            }

            if (attempt < 249)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), _timeProvider, cancellationToken);
            }
        }

        if (throwOnContentionTimeout)
        {
            throw new IOException("The capability catalog lock is unavailable.");
        }

        return false;
    }

    public void ReleaseLock()
    {
        _lock?.Dispose();
        _lock = null;
        _lockPath = null;
        _lockBindingIdentityMaterial = null;
    }

    public bool DirectoryExists(string path)
    {
        EnsureLockBinding();
        return GetDirectory(RequireContained(path), create: false) is not null;
    }

    public bool FileExists(string path)
    {
        EnsureLockBinding();
        var safePath = RequireContained(path);
        var parent = GetDirectory(Path.GetDirectoryName(safePath)!, create: false);
        if (parent is null)
        {
            return false;
        }

        using var handle = OpenRegularFile(safePath, parent, Path.GetFileName(safePath), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, writeThrough: false);
        return handle is not null;
    }

    public bool TryEnumerateDirectories(string path, int maximumEntries, out IReadOnlyList<string> names)
    {
        EnsureLockBinding();
        if (maximumEntries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        }

        var safePath = RequireContained(path);
        var directory = GetDirectory(safePath, create: false);
        if (directory is null)
        {
            names = [];
            return true;
        }

        var directories = new List<string>();
        var entryCount = 0;
        var entries = EnumerateEntries(safePath, directory, ProbeLimit(maximumEntries));
        foreach (var entry in entries)
        {
            if (entryCount >= maximumEntries)
            {
                names = [];
                return false;
            }
            entryCount++;

            if (entry.Kind is CapabilityCatalogDirectoryEntryKind.Unsafe or CapabilityCatalogDirectoryEntryKind.Unknown)
            {
                throw new IOException("Capability catalog directory enumeration refuses reparse points.");
            }
            if (entry.Kind != CapabilityCatalogDirectoryEntryKind.Directory)
            {
                continue;
            }
            var name = entry.Name;
            if (string.IsNullOrEmpty(name) || GetDirectory(Path.Combine(safePath, name), create: false) is null)
            {
                throw new IOException("A capability catalog directory entry disappeared during bound enumeration.");
            }

            directories.Add(name);
        }

        names = directories.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        return true;
    }

    public bool TryEnumerateStrictDirectories(string path, int maximumEntries, out IReadOnlyList<string> names)
    {
        EnsureLockBinding();
        if (maximumEntries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        }

        var safePath = RequireContained(path);
        var directory = GetDirectory(safePath, create: false);
        if (directory is null)
        {
            names = [];
            return true;
        }

        var directories = new List<string>();
        var entries = EnumerateEntries(safePath, directory, ProbeLimit(maximumEntries));
        foreach (var entry in entries)
        {
            if (directories.Count >= maximumEntries)
            {
                names = [];
                return false;
            }
            if (entry.Kind != CapabilityCatalogDirectoryEntryKind.Directory || string.IsNullOrEmpty(entry.Name) || GetDirectory(Path.Combine(safePath, entry.Name), create: false) is null)
            {
                throw new IOException("The staged artifact root contains an unexpected, linked, special, or disappearing entry.");
            }
            directories.Add(entry.Name);
        }

        names = directories.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        return true;
    }

    public IReadOnlyList<CapabilityCatalogDirectoryEntry> EnumerateBoundEntries(string path, int maximumEntries)
    {
        EnsureLockBinding();
        if (maximumEntries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        }

        var safePath = RequireContained(path);
        var directory = GetDirectory(safePath, create: false) ?? throw new DirectoryNotFoundException("The staged artifact directory is unavailable.");
        var results = new List<CapabilityCatalogDirectoryEntry>();
        foreach (var entry in EnumerateEntries(safePath, directory, ProbeLimit(maximumEntries)))
        {
            if (results.Count >= maximumEntries)
            {
                throw new IOException("The bounded staged artifact directory entry quota is exhausted.");
            }
            if (entry.Kind is CapabilityCatalogDirectoryEntryKind.Unsafe or CapabilityCatalogDirectoryEntryKind.Unknown || string.IsNullOrEmpty(entry.Name))
            {
                throw new IOException("The staged artifact directory contains a linked, special, or malformed entry.");
            }
            if (entry.Kind == CapabilityCatalogDirectoryEntryKind.Directory)
            {
                _ = GetDirectory(Path.Combine(safePath, entry.Name), create: false) ?? throw new IOException("A staged artifact directory disappeared during bound enumeration.");
            }
            else
            {
                var entryPath = Path.Combine(safePath, entry.Name);
                if (OperatingSystem.IsWindows()
                    && _lock is not null
                    && string.Equals(entryPath, _lockPath, _comparison))
                {
                    // Windows cannot reopen a FileShare.None lock, so its retained handle is the exact binding proof.
                    CapabilityCatalogNativeFileSystem.RequireSingleLink(_lock.SafeFileHandle, entry.Name);
                }
                else
                {
                    using var file = OpenRegularFile(entryPath, directory, entry.Name, FileMode.Open, FileAccess.Read, FileShare.Read, writeThrough: false) ?? throw new IOException("A staged artifact file disappeared during bound enumeration.");
                    CapabilityCatalogNativeFileSystem.RequireSingleLink(file, entry.Name);
                }
            }
            results.Add(entry);
        }
        return results.OrderBy(entry => entry.Name, StringComparer.Ordinal).ToArray();
    }

    public void PrepareDirectory(string path)
    {
        EnsureLockBinding();
        _ = GetDirectory(RequireContained(path), create: true) ?? throw new IOException("Capability catalog directory could not be prepared safely.");
    }

    /// <summary>Returns a retained, no-follow directory handle after proving its path is still bound to this session.</summary>
    /// <remarks>The caller must not dispose the returned handle. Its lifetime is owned by this session.</remarks>
    internal SafeFileHandle RequireBoundDirectory(string path)
    {
        EnsureLockBinding();
        var safePath = RequireContained(path);
        var directory = GetDirectory(safePath, create: false) ?? throw new DirectoryNotFoundException("Capability catalog directory is unavailable for retained publication.");
        EnsurePhysicalDirectoryBinding(safePath);
        EnsureLockBinding();
        return directory;
    }

    /// <summary>Revalidates that a retained directory still has the physical binding named by its canonical path.</summary>
    internal void RevalidateBoundDirectory(string path)
    {
        EnsureLockBinding();
        EnsurePhysicalDirectoryBinding(path);
        EnsureLockBinding();
    }

    /// <summary>Checks an exact regular file through a revalidated retained parent without following links.</summary>
    internal bool FileExistsBound(string path)
    {
        EnsureLockBinding();
        var safePath = RequireContained(path);
        var parentPath = Path.GetDirectoryName(safePath)!;
        var parent = GetDirectory(parentPath, create: false);
        if (parent is null)
        {
            return false;
        }

        EnsurePhysicalDirectoryBinding(parentPath);
        using var handle = OpenRegularFile(safePath, parent, Path.GetFileName(safePath), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, writeThrough: false);
        if (handle is null)
        {
            EnsurePhysicalDirectoryBinding(parentPath);
            return false;
        }

        CapabilityCatalogNativeFileSystem.RequireSingleLink(handle, Path.GetFileName(safePath));
        EnsurePhysicalDirectoryBinding(parentPath);
        EnsureLockBinding();
        return true;
    }

    public async Task<byte[]> ReadAllBytesAsync(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        return (await ReadAllBytesAsync(path, maximumBytes, allowEmpty: false, missingIsNull: false, cancellationToken))!;
    }

    public async Task<byte[]> ReadAllBytesAllowEmptyAsync(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        return (await ReadAllBytesAsync(path, maximumBytes, allowEmpty: true, missingIsNull: false, cancellationToken))!;
    }

    public Task<byte[]?> TryReadAllBytesAllowEmptyAsync(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        return ReadAllBytesAsync(path, maximumBytes, allowEmpty: true, missingIsNull: true, cancellationToken);
    }

    public Task<byte[]?> TryReadAllBytesAsync(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        return ReadAllBytesAsync(path, maximumBytes, allowEmpty: false, missingIsNull: true, cancellationToken);
    }

    public Task<byte[]?> TryReadAllBytesBoundAsync(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        return ReadAllBytesAsync(path, maximumBytes, allowEmpty: false, missingIsNull: true, cancellationToken, requireStableBinding: true);
    }

    public Task<byte[]?> TryReadAllBytesAllowEmptyBoundAsync(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        return ReadAllBytesAsync(path, maximumBytes, allowEmpty: true, missingIsNull: true, cancellationToken, requireStableBinding: true);
    }

    public FileStream OpenBoundReadLease(string path, int maximumBytes)
    {
        EnsureLockBinding();
        var safePath = RequireContained(path);
        var parentPath = Path.GetDirectoryName(safePath)!;
        var parent = GetDirectory(parentPath, create: false);
        if (parent is null)
        {
            throw new DirectoryNotFoundException("Capability catalog artifact parent is unavailable.");
        }
        EnsurePhysicalDirectoryBinding(parentPath);
        var handle = OpenRegularFile(safePath, parent, Path.GetFileName(safePath), FileMode.Open, FileAccess.Read, FileShare.Read, writeThrough: false) ?? throw new FileNotFoundException("Capability catalog artifact is missing.", safePath);
        CapabilityCatalogNativeFileSystem.RequireSingleLink(handle, Path.GetFileName(safePath));
        FileStream? stream = null;
        try
        {
            stream = new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
            if (stream.Length <= 0 || stream.Length > maximumBytes)
            {
                throw new FormatException("The capability catalog artifact is empty or exceeds its bounded size.");
            }
            var physicalIdentity = CapabilityCatalogNativeFileSystem.GetPathBindingIdentityMaterial(stream.SafeFileHandle);
            EnsurePhysicalDirectoryBinding(parentPath);
            using var revalidated = OpenRegularFile(safePath, parent, Path.GetFileName(safePath), FileMode.Open, FileAccess.Read, FileShare.Read, writeThrough: false) ?? throw new IOException("Capability catalog artifact disappeared while opening an execution lease.");
            if (!string.Equals(physicalIdentity, CapabilityCatalogNativeFileSystem.GetPathBindingIdentityMaterial(revalidated), StringComparison.Ordinal))
            {
                throw new IOException("Capability catalog artifact was substituted while opening an execution lease.");
            }
            var result = stream;
            stream = null;
            EnsureLockBinding();
            return result;
        }
        finally
        {
            stream?.Dispose();
        }
    }

    public FileStream OpenBoundUpdateLease(string path)
    {
        EnsureLockBinding();
        var safePath = RequireContained(path);
        var parentPath = Path.GetDirectoryName(safePath)!;
        var parent = GetDirectory(parentPath, create: false) ?? throw new DirectoryNotFoundException("Capability catalog artifact parent is unavailable.");
        EnsurePhysicalDirectoryBinding(parentPath);
        var name = Path.GetFileName(safePath);
        SafeFileHandle? handle = OpenRegularFile(safePath, parent, name, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, writeThrough: true) ?? throw new IOException("Capability catalog update artifact could not be opened safely.");
        FileStream? stream = null;
        try
        {
            CapabilityCatalogNativeFileSystem.RequireSingleLink(handle, name);
            stream = new FileStream(handle, FileAccess.ReadWrite, 4096, isAsync: false);
            handle = null;
            EnsureBoundUpdateLease(safePath, stream);
            var result = stream;
            stream = null;
            return result;
        }
        finally
        {
            stream?.Dispose();
            handle?.Dispose();
        }
    }

    public void EnsureBoundUpdateLease(string path, FileStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        EnsureLockBinding();
        var safePath = RequireContained(path);
        var parentPath = Path.GetDirectoryName(safePath)!;
        var parent = GetDirectory(parentPath, create: false) ?? throw new DirectoryNotFoundException("Capability catalog update artifact parent is unavailable.");
        EnsurePhysicalDirectoryBinding(parentPath);
        var name = Path.GetFileName(safePath);
        CapabilityCatalogNativeFileSystem.RequireSingleLink(stream.SafeFileHandle, name);
        var expected = CapabilityCatalogNativeFileSystem.GetPathBindingIdentityMaterial(stream.SafeFileHandle);
        using var current = OpenRegularFile(safePath, parent, name, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, writeThrough: false)
            ?? throw new IOException("Capability catalog update artifact disappeared during its retained lease.");
        CapabilityCatalogNativeFileSystem.RequireSingleLink(current, name);
        if (!string.Equals(expected, CapabilityCatalogNativeFileSystem.GetPathBindingIdentityMaterial(current), StringComparison.Ordinal))
        {
            throw new IOException("Capability catalog update artifact was substituted during its retained lease.");
        }

        EnsurePhysicalDirectoryBinding(parentPath);
        EnsureLockBinding();
    }

    private async Task<byte[]?> ReadAllBytesAsync(string path, int maximumBytes, bool allowEmpty, bool missingIsNull, CancellationToken cancellationToken, bool requireStableBinding = false)
    {
        EnsureLockBinding();
        var safePath = RequireContained(path);
        var parentPath = Path.GetDirectoryName(safePath)!;
        var parent = GetDirectory(parentPath, create: false);
        if (parent is null)
        {
            if (missingIsNull)
            {
                return null;
            }

            throw new DirectoryNotFoundException("Capability catalog artifact parent is unavailable.");
        }
        if (requireStableBinding)
        {
            EnsurePhysicalDirectoryBinding(parentPath);
        }
        var handle = OpenRegularFile(safePath, parent, Path.GetFileName(safePath), FileMode.Open, FileAccess.Read, FileShare.Read, writeThrough: false);
        if (handle is null)
        {
            if (missingIsNull)
            {
                return null;
            }

            throw new FileNotFoundException("Capability catalog artifact is missing.", safePath);
        }
        await using var stream = new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
        if (requireStableBinding)
        {
            CapabilityCatalogNativeFileSystem.RequireSingleLink(stream.SafeFileHandle, Path.GetFileName(safePath));
        }
        var physicalIdentity = requireStableBinding ? CapabilityCatalogNativeFileSystem.GetPathBindingIdentityMaterial(stream.SafeFileHandle) : null;
        if (stream.Length > int.MaxValue || stream.Length > maximumBytes || !allowEmpty && stream.Length <= 0)
        {
            throw new FormatException("The capability catalog artifact is empty or exceeds its bounded size.");
        }

        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        if (requireStableBinding)
        {
            EnsurePhysicalDirectoryBinding(parentPath);
            using var revalidated = OpenRegularFile(safePath, parent, Path.GetFileName(safePath), FileMode.Open, FileAccess.Read, FileShare.Read, writeThrough: false) ?? throw new IOException("Capability catalog artifact disappeared during bound read.");
            if (!string.Equals(physicalIdentity, CapabilityCatalogNativeFileSystem.GetPathBindingIdentityMaterial(revalidated), StringComparison.Ordinal))
            {
                throw new IOException("Capability catalog artifact was substituted during bound read.");
            }
        }
        EnsureLockBinding();
        return bytes;
    }

    public Task WriteTextAtomicallyAsync(string path, string content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        return WriteBytesAtomicallyAsync(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content), cancellationToken);
    }

    public void SetUserOnlyFilePermissions(string path)
    {
        EnsureLockBinding();
        var safePath = RequireContained(path);
        var parent = GetDirectory(Path.GetDirectoryName(safePath)!, create: false) ?? throw new DirectoryNotFoundException("Capability catalog file parent is unavailable.");
        using var handle = OpenRegularFile(safePath, parent, Path.GetFileName(safePath), FileMode.Open, FileAccess.ReadWrite, FileShare.Read, writeThrough: false) ?? throw new FileNotFoundException("Capability catalog file is missing.", safePath);
        CapabilityCatalogNativeFileSystem.SetUserOnlyPermissions(handle);
    }

    public async Task WriteBytesAtomicallyAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        EnsureLockBinding();
        var safePath = RequireContained(path);
        var parentPath = Path.GetDirectoryName(safePath)!;
        var parent = GetDirectory(parentPath, create: true) ?? throw new IOException("Capability catalog artifact parent could not be prepared safely.");
        var temporaryName = $".{Path.GetFileName(safePath)}.{Guid.NewGuid():N}.tmp";
        var temporaryPath = Path.Combine(parentPath, temporaryName);
        try
        {
            var handle = OpenRegularFile(temporaryPath, parent, temporaryName, FileMode.CreateNew, FileAccess.Write, FileShare.None, writeThrough: true) ?? throw new IOException("Capability catalog temporary artifact could not be created safely.");
            await using (var stream = new FileStream(handle, FileAccess.Write, 16 * 1024, isAsync: false))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                CapabilityCatalogNativeFileSystem.FlushToDisk(stream);
            }

            EnsureLockBinding();
            CapabilityCatalogNativeFileSystem.MoveFile(temporaryPath, safePath, parent, temporaryName, Path.GetFileName(safePath));
            await _durabilityBarrier.FlushAfterRenameAsync(safePath, parent);
            EnsureLockBinding();
        }
        finally
        {
            CapabilityCatalogNativeFileSystem.DeleteFileIfPresent(temporaryPath, parent, temporaryName);
        }
    }

    public async Task<bool> WriteBytesImmutablyAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        EnsureLockBinding();
        var safePath = RequireContained(path);
        var parentPath = Path.GetDirectoryName(safePath)!;
        var parent = GetDirectory(parentPath, create: true) ?? throw new IOException("Capability catalog immutable-artifact parent could not be prepared safely.");
        var destinationName = Path.GetFileName(safePath);
        var existing = await TryReadAllBytesBoundAsync(safePath, content.Length, cancellationToken);
        if (existing is not null)
        {
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(existing, content))
            {
                throw new IOException("Capability catalog immutable-artifact identity is already bound to different bytes.");
            }
            EnsureLockBinding();
            await _durabilityBarrier.FlushAfterRenameAsync(safePath, parent);
            EnsureLockBinding();
            return false;
        }

        var contentDigest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();
        var readyName = $".{destinationName}.{contentDigest}.ready";
        var readyPath = Path.Combine(parentPath, readyName);
        var writingName = $".{destinationName}.{Guid.NewGuid():N}.writing";
        var writingPath = Path.Combine(parentPath, writingName);
        var ownsWriting = false;
        var mayCleanupReady = false;
        try
        {
            var ready = await TryReadAllBytesBoundAsync(readyPath, content.Length, cancellationToken);
            if (ready is null)
            {
                var handle = OpenRegularFile(writingPath, parent, writingName, FileMode.CreateNew, FileAccess.Write, FileShare.None, writeThrough: true) ?? throw new IOException("Capability catalog immutable-artifact writing stage could not be created safely.");
                ownsWriting = true;
                await using (var stream = new FileStream(handle, FileAccess.Write, 16 * 1024, isAsync: false))
                {
                    await stream.WriteAsync(content, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    CapabilityCatalogNativeFileSystem.FlushToDisk(stream);
                }

                if (CapabilityCatalogNativeFileSystem.TryMoveFileNoReplace(writingPath, readyPath, parent, writingName, readyName))
                {
                    ownsWriting = false;
                    mayCleanupReady = true;
                    await _durabilityBarrier.FlushAfterRenameAsync(readyPath, parent);
                    ready = await ReadAllBytesAsync(readyPath, content.Length, allowEmpty: content.Length == 0, missingIsNull: false, cancellationToken, requireStableBinding: true);
                    if (ready is null || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(ready, content))
                    {
                        throw new IOException("Capability catalog immutable-artifact ready stage changed after retained-handle publication.");
                    }
                }
                else
                {
                    ready = await ReadAllBytesAsync(readyPath, content.Length, allowEmpty: content.Length == 0, missingIsNull: false, cancellationToken, requireStableBinding: true);
                    if (ready is null || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(ready, content))
                    {
                        throw new IOException("Capability catalog immutable-artifact ready stage is bound to different bytes.");
                    }
                    mayCleanupReady = true;
                }
            }
            else
            {
                if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(ready, content))
                {
                    throw new IOException("Capability catalog immutable-artifact ready stage is bound to different bytes.");
                }
                mayCleanupReady = true;
            }

            EnsureLockBinding();
            if (CapabilityCatalogNativeFileSystem.TryMoveFileNoReplace(readyPath, safePath, parent, readyName, destinationName))
            {
                mayCleanupReady = false;
                try
                {
                    var published = await ReadAllBytesAsync(safePath, content.Length, allowEmpty: content.Length == 0, missingIsNull: false, cancellationToken, requireStableBinding: true);
                    if (published is null || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(published, content))
                    {
                        throw new IOException("Capability catalog immutable artifact changed during retained-handle publication.");
                    }
                }
                catch
                {
                    CapabilityCatalogNativeFileSystem.DeleteFileIfPresent(safePath, parent, destinationName);
                    throw;
                }
                await _durabilityBarrier.FlushAfterRenameAsync(safePath, parent);
                EnsureLockBinding();
                return true;
            }

            existing = await ReadAllBytesAsync(safePath, content.Length, allowEmpty: content.Length == 0, missingIsNull: false, cancellationToken, requireStableBinding: true);
            if (existing is null || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(existing, content))
            {
                throw new IOException("Capability catalog immutable-artifact identity is already bound to different bytes.");
            }

            await _durabilityBarrier.FlushAfterRenameAsync(safePath, parent);
            EnsureLockBinding();
            return false;
        }
        finally
        {
            if (ownsWriting)
            {
                CapabilityCatalogNativeFileSystem.DeleteFileIfPresent(writingPath, parent, writingName);
            }
            if (mayCleanupReady)
            {
                CapabilityCatalogNativeFileSystem.DeleteFileIfPresent(readyPath, parent, readyName);
            }
        }
    }

    public IReadOnlyList<(string Name, long Length)> EnumerateRegularFiles(string path, int maximumEntries, long maximumBytes)
    {
        EnsureLockBinding();
        if (maximumEntries < 0 || maximumBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntries), "Capability catalog enumeration bounds cannot be negative.");
        }

        var safePath = RequireContained(path);
        var directory = GetDirectory(safePath, create: false);
        if (directory is null)
        {
            return [];
        }

        var entries = new List<(string Name, long Length)>();
        var totalBytes = 0L;
        var directoryEntries = EnumerateEntries(safePath, directory, ProbeLimit(maximumEntries));
        foreach (var entry in directoryEntries)
        {
            if (entries.Count >= maximumEntries)
            {
                throw new IOException("The bounded capability catalog trust-root entry quota is exhausted.");
            }

            if (entry.Kind != CapabilityCatalogDirectoryEntryKind.RegularFile)
            {
                throw new IOException("The capability catalog trust root contains a non-regular entry.");
            }
            var name = entry.Name;
            var fullPath = Path.Combine(safePath, name);
            if (OperatingSystem.IsWindows()
                && _lock is not null
                && string.Equals(fullPath, _lockPath, _comparison))
            {
                CapabilityCatalogNativeFileSystem.RequireSingleLink(_lock.SafeFileHandle, name);
                if (_lock.Length > maximumBytes - totalBytes)
                {
                    throw new IOException("The bounded capability catalog trust-root byte quota is exhausted.");
                }
                totalBytes += _lock.Length;
                entries.Add((name, _lock.Length));
                continue;
            }
            var handle = OpenRegularFile(fullPath, directory, name, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, writeThrough: false) ?? throw new IOException("A capability catalog trust-root entry disappeared during bounded enumeration.");
            using var stream = new FileStream(handle, FileAccess.Read, 1, isAsync: false);
            CapabilityCatalogNativeFileSystem.RequireSingleLink(stream.SafeFileHandle, name);
            if (stream.Length > maximumBytes - totalBytes)
            {
                throw new IOException("The bounded capability catalog trust-root byte quota is exhausted.");
            }
            totalBytes += stream.Length;
            entries.Add((name, stream.Length));
        }

        EnsureLockBinding();
        return entries;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        ReleaseLock();
        for (var index = _ownedDirectories.Count - 1; index >= 0; index--)
        {
            _ownedDirectories[index].Dispose();
        }
    }

    private SafeFileHandle? GetDirectory(string path, bool create)
    {
        var safePath = RequireContained(path);
        var relative = Path.GetRelativePath(_root, safePath);
        if (relative == ".")
        {
            return _directories[string.Empty];
        }

        var parent = _directories[string.Empty];
        var currentPath = _root;
        var keySegments = new List<string>();
        foreach (var segment in Split(relative))
        {
            keySegments.Add(segment);
            var key = string.Join(Path.DirectorySeparatorChar, keySegments);
            currentPath = Path.Combine(currentPath, segment);
            if (_directories.TryGetValue(key, out var retained))
            {
                parent = retained;
                continue;
            }

            _pathObserver?.BeforeDirectoryChildOpen(Path.GetDirectoryName(currentPath)!, segment);
            var opened = CapabilityCatalogNativeFileSystem.OpenDirectory(currentPath, parent, segment, create, _durabilityBarrier, out var created);
            if (opened is null)
            {
                return null;
            }

            _ownedDirectories.Add(opened);
            if (created)
            {
                _durabilityBarrier.FlushAfterDirectoryCreate(currentPath, parent);
            }
            _directories[key] = opened;
            parent = opened;
        }

        return parent;
    }

    private static IEnumerable<CapabilityCatalogDirectoryEntry> EnumerateEntries(string safePath, SafeFileHandle directory, int maximumEntries)
    {
        CapabilityCatalogNativeFileSystem.RewindDirectoryEnumeration(directory);
        return OperatingSystem.IsWindows()
            ? CapabilityCatalogNativeFileSystem.EnumerateWindowsDirectory(directory, maximumEntries)
            : OperatingSystem.IsMacOS()
                ? CapabilityCatalogNativeFileSystem.EnumerateMacDirectory(directory, maximumEntries)
                : Directory.EnumerateFileSystemEntries(CapabilityCatalogNativeFileSystem.GetDirectoryEnumerationPath(directory), "*", SearchOption.TopDirectoryOnly).Take(maximumEntries).Select(entry => new CapabilityCatalogDirectoryEntry(Path.GetFileName(entry), (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0 ? CapabilityCatalogDirectoryEntryKind.Unsafe : (File.GetAttributes(entry) & FileAttributes.Directory) != 0 ? CapabilityCatalogDirectoryEntryKind.Directory : CapabilityCatalogDirectoryEntryKind.RegularFile));
    }

    private static int ProbeLimit(int maximumEntries) => maximumEntries == int.MaxValue ? int.MaxValue : maximumEntries + 1;

    private static bool IsTransientInitializationRace(IOException exception) => (exception.HResult & 0xFFFF) is 2 or 13;

    private SafeFileHandle? OpenRegularFile(string fullPath, SafeFileHandle parent, string name, FileMode mode, FileAccess access, FileShare share, bool writeThrough)
    {
        var parentPath = Path.GetDirectoryName(fullPath)!;
        _pathObserver?.BeforeFileChildOpen(parentPath, name);
        SafeFileHandle? handle = null;
        try
        {
            handle = CapabilityCatalogNativeFileSystem.OpenRegularFile(fullPath, parent, name, mode, access, share, writeThrough);
        }
        finally
        {
            try
            {
                _pathObserver?.AfterFileChildOpen(parentPath, name);
            }
            catch
            {
                handle?.Dispose();
                throw;
            }
        }
        return handle;
    }

    private void EnsurePhysicalDirectoryBinding(string path)
    {
        var safePath = RequireContained(path);
        var expected = GetDirectory(safePath, create: false) ?? throw new DirectoryNotFoundException("Capability catalog directory is unavailable for physical binding validation.");
        using var current = Open(safePath, _comparison, createRoot: false) ?? throw new IOException("Capability catalog directory disappeared during physical binding validation.");
        if (!string.Equals(CapabilityCatalogNativeFileSystem.GetPathBindingIdentityMaterial(expected), current.PathBindingIdentityMaterial, StringComparison.Ordinal))
        {
            throw new IOException("Capability catalog directory was substituted during bound read.");
        }
    }

    private void EnsureLockBinding()
    {
        if (_lock is null)
        {
            return;
        }

        var lockPath = _lockPath ?? throw new IOException("Capability catalog mutation lock identity is incomplete.");
        var name = Path.GetFileName(lockPath);
        if (OperatingSystem.IsWindows())
        {
            CapabilityCatalogNativeFileSystem.RequireSingleLink(_lock.SafeFileHandle, name);
            return;
        }

        var expected = _lockBindingIdentityMaterial ?? throw new IOException("Capability catalog POSIX mutation lock identity is incomplete.");
        var parentPath = Path.GetDirectoryName(lockPath) ?? throw new IOException("Capability catalog mutation lock has no parent directory.");
        var parent = GetDirectory(parentPath, create: false) ?? throw new IOException("Capability catalog mutation lock parent disappeared during the active session.");
        using var current = OpenRegularFile(lockPath, parent, name, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, writeThrough: false);
        if (current is null)
        {
            throw new IOException("Capability catalog mutation lock disappeared during the active session.");
        }

        CapabilityCatalogNativeFileSystem.RequireSingleLink(current, name);
        var actual = CapabilityCatalogNativeFileSystem.GetPathBindingIdentityMaterial(current);
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new IOException("Capability catalog mutation lock was replaced during the active session.");
        }
    }

    private string RequireContained(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var candidate = Path.GetFullPath(path);
        if (!string.Equals(_root, candidate, _comparison))
        {
            var rootWithSeparator = Path.EndsInDirectorySeparator(_root) ? _root : _root + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(rootWithSeparator, _comparison))
            {
                throw new IOException("Capability catalog persistence path escaped its configured root.");
            }
        }

        return candidate;
    }

    private static IEnumerable<string> Split(string relative)
    {
        return relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
    }
}
