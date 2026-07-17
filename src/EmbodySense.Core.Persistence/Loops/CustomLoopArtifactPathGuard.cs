using System.Text;

namespace EmbodySense.Core.Persistence.Loops;

internal sealed class CustomLoopArtifactPathGuard
{
    private readonly string _workspaceRoot;
    private readonly StringComparison _pathComparison;

    public CustomLoopArtifactPathGuard(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }

    public bool DirectoryExists(string root)
    {
        var safeRoot = ValidateRoot(root);
        EnsureNoReparsePoints(safeRoot);
        return Directory.Exists(safeRoot);
    }

    public void PrepareRoot(string root)
    {
        var safeRoot = ValidateRoot(root);
        EnsureNoReparsePoints(safeRoot);
        Directory.CreateDirectory(safeRoot);
        EnsureNoReparsePoints(safeRoot);
    }

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

    public FileStream AcquireExclusiveMutationLock(string root)
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
        catch (IOException exception)
        {
            stream?.Dispose();
            throw new InvalidOperationException("Custom-loop persistence is locked by another process; the mutation failed closed.", exception);
        }
        catch
        {
            stream?.Dispose();
            throw;
        }
    }

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

    public void DeleteFile(string root, string path)
    {
        EnsureContained(ValidateRoot(root), Path.GetFullPath(path), "Artifact path escaped its configured root.");
        EnsureNoReparsePoints(path);
        File.Delete(path);
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
