using System.Text;

namespace EmbodySense.Core.Persistence.Loops;

/// <summary>
/// Enforces workspace containment, rejects reparse-point traversal, and supplies bounded durable artifact I/O.
/// </summary>
/// <remarks>
/// Every path is canonicalized beneath the configured workspace root before use. Mutations use an exclusive cross-process
/// file lease and write-through sibling temporary files whose rename is the single-artifact commit boundary. Unsafe,
/// oversized, or structurally ambiguous artifacts fail closed.
/// </remarks>
internal sealed class CustomLoopArtifactPathGuard
{
    private const int ReadLockMaximumAttempts = 9;
    private static readonly TimeSpan _readLockRetryDelay = TimeSpan.FromMilliseconds(25);
    private readonly string _workspaceRoot;
    private readonly StringComparison _pathComparison;

    /// <summary>
    /// Initializes a new instance of the <see cref="CustomLoopArtifactPathGuard"/> type.
    /// </summary>
    /// <param name="workspaceRoot">The absolute workspace root path.</param>
    public CustomLoopArtifactPathGuard(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }

    /// <summary>
    /// Determines whether a contained, non-reparse artifact directory exists.
    /// </summary>
    /// <param name="root">The root.</param>
    /// <returns><see langword="true"/> when the validated directory exists; otherwise, <see langword="false"/>.</returns>
    public bool DirectoryExists(string root)
    {
        var safeRoot = ValidateRoot(root);
        EnsureNoReparsePoints(safeRoot);
        return Directory.Exists(safeRoot);
    }

    /// <summary>
    /// Validates and creates a contained artifact root, rejecting reparse points before and after creation.
    /// </summary>
    /// <param name="root">The root.</param>
    public void PrepareRoot(string root)
    {
        var safeRoot = ValidateRoot(root);
        EnsureNoReparsePoints(safeRoot);
        Directory.CreateDirectory(safeRoot);
        EnsureNoReparsePoints(safeRoot);
    }

    /// <summary>
    /// Resolves and validates one direct artifact path beneath a configured root.
    /// </summary>
    /// <param name="root">The root.</param>
    /// <param name="fileName">The file name.</param>
    /// <returns>The canonical contained path after reparse-point validation.</returns>
    public string GetFilePath(string root, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var safeRoot = ValidateRoot(root);
        EnsureNoReparsePoints(safeRoot);
        var path = Path.GetFullPath(Path.Combine(safeRoot, fileName));
        EnsureContained(safeRoot, path, "Artifact path escaped its configured root.");
        EnsureNoReparsePoints(path);
        return path;
    }

    /// <summary>
    /// Acquires the exclusive cross-process mutation lease for one artifact root.
    /// </summary>
    /// <param name="root">The root.</param>
    /// <returns>The ownership stream; disposal releases the lease.</returns>
    public FileStream AcquireExclusiveMutationLock(string root)
    {
        try
        {
            return AcquireExclusiveLock(root);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException("Custom-loop persistence is locked by another process; the mutation failed closed.", exception);
        }
    }

    /// <summary>
    /// Acquires the exclusive cross-process lease for a consistent read, tolerating only a bounded short-lived mutation.
    /// </summary>
    /// <param name="root">The artifact root.</param>
    /// <param name="cancellationToken">The token used to cancel bounded contention waiting.</param>
    /// <returns>The ownership stream; disposal releases the lease.</returns>
    public async Task<FileStream> AcquireExclusiveReadLockAsync(string root, CancellationToken cancellationToken)
    {
        IOException? lastContention = null;
        for (var attempt = 1; attempt <= ReadLockMaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return AcquireExclusiveLock(root);
            }
            catch (IOException exception)
            {
                lastContention = exception;
                if (attempt < ReadLockMaximumAttempts)
                {
                    await Task.Delay(_readLockRetryDelay, cancellationToken);
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("Custom-loop persistence remained locked by another process after bounded read retries; the read failed closed.", lastContention);
    }

    private FileStream AcquireExclusiveLock(string root)
    {
        PrepareRoot(root);
        var lockPath = GetFilePath(root, ".custom-loop-mutations.lock");
        FileStream? stream = null;
        try
        {
            stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.WriteThrough);
            EnsureNoReparsePoints(lockPath);
            return stream;
        }
        catch
        {
            stream?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads one contained non-reparse artifact while enforcing the byte limit before and during the read.
    /// </summary>
    /// <param name="root">The root.</param>
    /// <param name="path">The path.</param>
    /// <param name="maximumBytes">The maximum bytes.</param>
    /// <param name="artifactName">The artifact name.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The complete artifact bytes.</returns>
    public async Task<byte[]> ReadAllBytesAsync(string root, string path, long maximumBytes, string artifactName, CancellationToken cancellationToken)
    {
        EnsureContained(ValidateRoot(root), Path.GetFullPath(path), "Artifact path escaped its configured root.");
        EnsureNoReparsePoints(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > maximumBytes)
        {
            throw new FormatException($"{artifactName} `{path}` exceeds the maximum artifact size of {maximumBytes} bytes.");
        }

        using var content = new MemoryStream(capacity: checked((int)stream.Length));
        var buffer = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (content.Length + read > maximumBytes)
            {
                throw new FormatException($"{artifactName} `{path}` exceeds the maximum artifact size of {maximumBytes} bytes.");
            }

            content.Write(buffer, 0, read);
        }

        EnsureNoReparsePoints(path);
        return content.ToArray();
    }

    /// <summary>
    /// Gets the length of one contained non-reparse artifact without opening or parsing its content.
    /// </summary>
    /// <param name="root">The artifact root.</param>
    /// <param name="path">The artifact path.</param>
    /// <returns>The observed byte length.</returns>
    public long GetFileLength(string root, string path)
    {
        EnsureContained(ValidateRoot(root), Path.GetFullPath(path), "Artifact path escaped its configured root.");
        EnsureNoReparsePoints(path);
        var length = new FileInfo(path).Length;
        EnsureNoReparsePoints(path);
        return length;
    }

    /// <summary>
    /// Flushes text to a sibling temporary file and renames it over the contained destination.
    /// </summary>
    /// <param name="root">The root.</param>
    /// <param name="path">The path.</param>
    /// <param name="content">The content.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task WriteTextAtomicallyAsync(string root, string path, string content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        PrepareRoot(root);
        EnsureContained(ValidateRoot(root), Path.GetFullPath(path), "Artifact path escaped its configured root.");
        EnsureNoReparsePoints(path);
        var tempPath = GetFilePath(root, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            EnsureNoReparsePoints(tempPath);
            EnsureNoReparsePoints(path);
            File.Move(tempPath, path, overwrite: true);
            EnsureNoReparsePoints(path);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                EnsureNoReparsePoints(tempPath);
                File.Delete(tempPath);
            }
        }
    }

    /// <summary>
    /// Flushes text to a sibling temporary file and atomically creates the contained destination only when it remains absent.
    /// </summary>
    /// <param name="root">The artifact root.</param>
    /// <param name="path">The destination path.</param>
    /// <param name="content">The UTF-8 content.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns><see langword="true"/> when this call created the destination; otherwise <see langword="false"/> without replacing pre-existing evidence.</returns>
    public async Task<bool> WriteTextAtomicallyIfAbsentAsync(string root, string path, string content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        PrepareRoot(root);
        EnsureContained(ValidateRoot(root), Path.GetFullPath(path), "Artifact path escaped its configured root.");
        EnsureNoReparsePoints(path);
        var tempPath = GetFilePath(root, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            EnsureNoReparsePoints(tempPath);
            EnsureNoReparsePoints(path);
            try
            {
                File.Move(tempPath, path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path))
            {
                return false;
            }

            EnsureNoReparsePoints(path);
            return true;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                EnsureNoReparsePoints(tempPath);
                File.Delete(tempPath);
            }
        }
    }

    /// <summary>
    /// Deletes one contained non-reparse artifact if it exists.
    /// </summary>
    /// <param name="root">The root.</param>
    /// <param name="path">The path.</param>
    public void DeleteFile(string root, string path)
    {
        EnsureContained(ValidateRoot(root), Path.GetFullPath(path), "Artifact path escaped its configured root.");
        EnsureNoReparsePoints(path);
        File.Delete(path);
    }

    /// <summary>
    /// Atomically moves one contained non-reparse artifact to an absent sibling path.
    /// </summary>
    /// <param name="root">The common artifact root.</param>
    /// <param name="sourcePath">The existing source path.</param>
    /// <param name="destinationPath">The absent destination path.</param>
    public void MoveFileIfDestinationAbsent(string root, string sourcePath, string destinationPath)
    {
        var safeRoot = ValidateRoot(root);
        EnsureContained(safeRoot, Path.GetFullPath(sourcePath), "Artifact source path escaped its configured root.");
        EnsureContained(safeRoot, Path.GetFullPath(destinationPath), "Artifact destination path escaped its configured root.");
        EnsureNoReparsePoints(sourcePath);
        EnsureNoReparsePoints(destinationPath);
        File.Move(sourcePath, destinationPath, overwrite: false);
        EnsureNoReparsePoints(destinationPath);
    }

    private string ValidateRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var safeRoot = Path.GetFullPath(root);
        EnsureContained(_workspaceRoot, safeRoot, "Custom-loop artifact root escaped the workspace.");
        return safeRoot;
    }

    private void EnsureContained(string root, string candidate, string message)
    {
        if (string.Equals(root, candidate, _pathComparison))
        {
            return;
        }

        var rootWithSeparator = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootWithSeparator, _pathComparison))
        {
            throw new InvalidOperationException(message);
        }
    }

    private void EnsureNoReparsePoints(string target)
    {
        var safeTarget = Path.GetFullPath(target);
        EnsureContained(_workspaceRoot, safeTarget, "Custom-loop artifact path escaped the workspace.");
        ThrowIfReparsePoint(_workspaceRoot);
        if (string.Equals(_workspaceRoot, safeTarget, _pathComparison))
        {
            return;
        }

        var relative = Path.GetRelativePath(_workspaceRoot, safeTarget);
        var current = _workspaceRoot;
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            ThrowIfReparsePoint(current);
        }
    }

    private static void ThrowIfReparsePoint(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException($"Custom-loop persistence refuses reparse points or junctions: `{path}`.");
            }
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
