using System.Text;
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
    private FileStream? _lock;

    private CapabilityCatalogPathSession(string root, StringComparison comparison, Dictionary<string, SafeFileHandle> directories, List<SafeFileHandle> ownedDirectories, ICapabilityCatalogDurabilityBarrier durabilityBarrier)
    {
        _root = root;
        _comparison = comparison;
        _directories = directories;
        _ownedDirectories = ownedDirectories;
        _durabilityBarrier = durabilityBarrier;
        PhysicalIdentityMaterial = CapabilityCatalogNativeFileSystem.GetPhysicalIdentityMaterial(directories[string.Empty]);
    }

    public string PhysicalIdentityMaterial { get; }

    public string Root => _root;

    public static CapabilityCatalogPathSession? Open(string root, StringComparison comparison, bool createRoot, ICapabilityCatalogDurabilityBarrier? durabilityBarrier = null)
    {
        durabilityBarrier ??= NativeCapabilityCatalogDurabilityBarrier.Instance;
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
            return new CapabilityCatalogPathSession(root, comparison, directories, owned, durabilityBarrier);
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

    public async Task AcquireLockAsync(string path, CancellationToken cancellationToken)
    {
        var safePath = RequireContained(path);
        var parentPath = Path.GetDirectoryName(safePath) ?? throw new IOException("Capability catalog lock has no parent directory.");
        var parent = GetDirectory(parentPath, create: true) ?? throw new IOException("Capability catalog lock parent is unavailable.");
        var name = Path.GetFileName(safePath);
        for (var attempt = 0; attempt < 250; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _lock = CapabilityCatalogNativeFileSystem.TryAcquireExclusiveLock(safePath, parent, name);
            if (_lock is not null)
            {
                return;
            }

            if (attempt < 249)
            {
                await Task.Delay(20, cancellationToken);
            }
        }

        throw new IOException("The capability catalog lock is unavailable.");
    }

    public bool DirectoryExists(string path)
    {
        return GetDirectory(RequireContained(path), create: false) is not null;
    }

    public bool FileExists(string path)
    {
        var safePath = RequireContained(path);
        var parent = GetDirectory(Path.GetDirectoryName(safePath)!, create: false);
        if (parent is null)
        {
            return false;
        }

        using var handle = CapabilityCatalogNativeFileSystem.OpenRegularFile(safePath, parent, Path.GetFileName(safePath), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, writeThrough: false);
        return handle is not null;
    }

    public void PrepareDirectory(string path)
    {
        _ = GetDirectory(RequireContained(path), create: true) ?? throw new IOException("Capability catalog directory could not be prepared safely.");
    }

    public async Task<byte[]> ReadAllBytesAsync(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        var safePath = RequireContained(path);
        var parent = GetDirectory(Path.GetDirectoryName(safePath)!, create: false) ?? throw new DirectoryNotFoundException("Capability catalog artifact parent is unavailable.");
        var handle = CapabilityCatalogNativeFileSystem.OpenRegularFile(safePath, parent, Path.GetFileName(safePath), FileMode.Open, FileAccess.Read, FileShare.Read, writeThrough: false) ?? throw new FileNotFoundException("Capability catalog artifact is missing.", safePath);
        await using var stream = new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
        if (stream.Length is <= 0 or > int.MaxValue || stream.Length > maximumBytes)
        {
            throw new FormatException("The capability catalog artifact is empty or exceeds its bounded size.");
        }

        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return bytes;
    }

    public Task WriteTextAtomicallyAsync(string path, string content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        return WriteBytesAtomicallyAsync(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content), cancellationToken);
    }

    public void SetUserOnlyFilePermissions(string path)
    {
        var safePath = RequireContained(path);
        var parent = GetDirectory(Path.GetDirectoryName(safePath)!, create: false) ?? throw new DirectoryNotFoundException("Capability catalog file parent is unavailable.");
        using var handle = CapabilityCatalogNativeFileSystem.OpenRegularFile(safePath, parent, Path.GetFileName(safePath), FileMode.Open, FileAccess.ReadWrite, FileShare.Read, writeThrough: false) ?? throw new FileNotFoundException("Capability catalog file is missing.", safePath);
        CapabilityCatalogNativeFileSystem.SetUserOnlyPermissions(handle);
    }

    public async Task WriteBytesAtomicallyAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var safePath = RequireContained(path);
        var parentPath = Path.GetDirectoryName(safePath)!;
        var parent = GetDirectory(parentPath, create: true) ?? throw new IOException("Capability catalog artifact parent could not be prepared safely.");
        var temporaryName = $".{Path.GetFileName(safePath)}.{Guid.NewGuid():N}.tmp";
        var temporaryPath = Path.Combine(parentPath, temporaryName);
        try
        {
            var handle = CapabilityCatalogNativeFileSystem.OpenRegularFile(temporaryPath, parent, temporaryName, FileMode.CreateNew, FileAccess.Write, FileShare.None, writeThrough: true) ?? throw new IOException("Capability catalog temporary artifact could not be created safely.");
            await using (var stream = new FileStream(handle, FileAccess.Write, 16 * 1024, isAsync: false))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                CapabilityCatalogNativeFileSystem.FlushToDisk(stream);
            }

            CapabilityCatalogNativeFileSystem.MoveFile(temporaryPath, safePath, parent, temporaryName, Path.GetFileName(safePath));
            await _durabilityBarrier.FlushAfterRenameAsync(safePath, parent);
        }
        finally
        {
            CapabilityCatalogNativeFileSystem.DeleteFileIfPresent(temporaryPath, parent, temporaryName);
        }
    }

    public IReadOnlyList<(string Name, long Length)> EnumerateRegularFiles(string path, int maximumEntries, long maximumBytes)
    {
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

        var enumerationPath = OperatingSystem.IsWindows() ? safePath : CapabilityCatalogNativeFileSystem.GetDirectoryEnumerationPath(directory);
        var entries = new List<(string Name, long Length)>();
        var totalBytes = 0L;
        foreach (var entry in Directory.EnumerateFileSystemEntries(enumerationPath))
        {
            if (entries.Count >= maximumEntries)
            {
                throw new IOException("The bounded capability catalog trust-root entry quota is exhausted.");
            }

            var name = Path.GetFileName(entry);
            var fullPath = Path.Combine(safePath, name);
            var handle = CapabilityCatalogNativeFileSystem.OpenRegularFile(fullPath, directory, name, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, writeThrough: false) ?? throw new IOException("A capability catalog trust-root entry disappeared during bounded enumeration.");
            using var stream = new FileStream(handle, FileAccess.Read, 1, isAsync: false);
            if (stream.Length > maximumBytes - totalBytes)
            {
                throw new IOException("The bounded capability catalog trust-root byte quota is exhausted.");
            }
            totalBytes += stream.Length;
            entries.Add((name, stream.Length));
        }

        return entries;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _lock?.Dispose();
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
