using System.Text;
using EmbodySense.Core.Persistence.Loops;
using Microsoft.Win32.SafeHandles;

namespace EmbodySense.Core.Persistence.WorkspaceActions;

/// <summary>Provides handle-relative creation, locking, and bounded I/O for workspace-action private artifacts.</summary>
internal sealed class WorkspaceActionPrivateArtifactPathGuard
{
    private const int ReadLockMaximumAttempts = 9;
    private static readonly TimeSpan _readLockRetryDelay = TimeSpan.FromMilliseconds(25);
    private readonly string _workspaceRoot;

    public WorkspaceActionPrivateArtifactPathGuard(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
    }

    public void PrepareRoot(string root)
    {
        using var directory = WorkspaceActionNativeFileSystem.OpenOrCreatePrivateDirectoryUnderWorkspace(_workspaceRoot, root);
        WorkspaceActionNativeFileSystem.RequirePrivateDirectoryPermissions(directory);
    }

    public async Task<WorkspaceActionPrivateArtifactLockLease> AcquireExclusiveReadLockAsync(
        string root,
        CancellationToken cancellationToken)
    {
        IOException? lastContention = null;
        for (var attempt = 1; attempt <= ReadLockMaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SafeFileHandle? directory = null;
            SafeFileHandle? lockHandle = null;
            FileStream? ownership = null;
            try
            {
                directory = WorkspaceActionNativeFileSystem.OpenOrCreatePrivateDirectoryUnderWorkspace(_workspaceRoot, root);
                WorkspaceActionNativeFileSystem.RequirePrivateDirectoryPermissions(directory);
                var directoryIdentity = WorkspaceActionNativeFileSystem.GetIdentity(directory);
                if (!OperatingSystem.IsWindows()
                    && !CustomLoopCrossProcessFileLock.TryAcquire(directory))
                {
                    lastContention = new IOException("Workspace action private directory is owned by another process.");
                    directory.Dispose();
                    directory = null;
                    if (attempt < ReadLockMaximumAttempts)
                    {
                        await Task.Delay(_readLockRetryDelay, cancellationToken).ConfigureAwait(false);
                    }
                    continue;
                }
                lockHandle = WorkspaceActionNativeFileSystem.OpenRelativeFileForUpdate(
                    directory,
                    ".custom-loop-mutations.lock",
                    allowMissing: false,
                    create: true,
                    shareForLocking: true,
                    denyDeleteSharing: true,
                    requireDeleteAccess: false)!;
                var lockIdentity = WorkspaceActionNativeFileSystem.GetIdentity(lockHandle);
                if (!lockIdentity.SameMount(directoryIdentity))
                {
                    throw new IOException("Workspace action private ownership refused a mounted file outside its retained directory.");
                }
                WorkspaceActionNativeFileSystem.RequirePrivateFilePermissions(lockHandle);
                if (RandomAccess.GetLength(lockHandle) != 0)
                {
                    throw new FormatException("Workspace action private ownership must remain value-free.");
                }
                ownership = new FileStream(lockHandle, FileAccess.ReadWrite, bufferSize: 1, isAsync: false);
                lockHandle = null;
                if (CustomLoopCrossProcessFileLock.TryAcquire(ownership))
                {
                    using var named = WorkspaceActionNativeFileSystem.OpenRelativeFile(
                        directory,
                        ".custom-loop-mutations.lock",
                        allowMissing: false,
                        write: false)!;
                    WorkspaceActionNativeFileSystem.RequireExactOpenedName(named, ".custom-loop-mutations.lock");
                    if (!WorkspaceActionNativeFileSystem.GetIdentity(named).SameEntry(lockIdentity))
                    {
                        throw new IOException("Workspace action private ownership was substituted while its lock was acquired.");
                    }
                    var retained = new WorkspaceActionPrivateArtifactLockLease(
                        directory,
                        directoryIdentity,
                        ownership,
                        lockIdentity,
                        _workspaceRoot,
                        root);
                    directory = null;
                    ownership = null;
                    return retained;
                }
                lastContention = new IOException("Workspace action private storage is owned by another process.");
            }
            finally
            {
                ownership?.Dispose();
                lockHandle?.Dispose();
                directory?.Dispose();
            }
            if (attempt < ReadLockMaximumAttempts)
            {
                await Task.Delay(_readLockRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(
            "Workspace action private storage remained locked by another process after bounded retries.",
            lastContention);
    }

    public IReadOnlyList<string> EnumerateNames(
        WorkspaceActionPrivateArtifactLockLease ownership,
        int maximumEntries)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        return WorkspaceActionNativeFileSystem.EnumerateRelativeNames(ownership.DirectoryHandle, maximumEntries);
    }

    public bool FileExists(WorkspaceActionPrivateArtifactLockLease ownership, string fileName, bool allowMultipleLinks = false)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        EnsureSimpleName(fileName);
        using var file = WorkspaceActionNativeFileSystem.OpenRelativeFile(
            ownership.DirectoryHandle,
            fileName,
            allowMissing: true,
            write: false,
            allowMultipleLinks: allowMultipleLinks);
        return file is not null;
    }

    public async Task<bool> WriteTextAtomicallyIfAbsentAsync(
        WorkspaceActionPrivateArtifactLockLease ownership,
        string fileName,
        string content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        ArgumentNullException.ThrowIfNull(content);
        EnsureSimpleName(fileName);
        var directory = ownership.DirectoryHandle;
        var directoryIdentity = WorkspaceActionNativeFileSystem.GetIdentity(directory);
        var temporaryName = $".{fileName}.{Guid.NewGuid():N}.tmp";
        SafeFileHandle? temporary = null;
        var published = false;
        try
        {
            temporary = WorkspaceActionNativeFileSystem.CreateRelativeFile(directory, temporaryName, privateSecurityAccess: true);
            if (!WorkspaceActionNativeFileSystem.GetIdentity(temporary).SameMount(directoryIdentity))
            {
                throw new IOException("Workspace action private temporary refused a mounted file outside its retained directory.");
            }
            WorkspaceActionNativeFileSystem.RequirePrivateFilePermissions(temporary);
            await WorkspaceActionNativeFileSystem.WriteAllBytesAsync(
                temporary,
                Encoding.UTF8.GetBytes(content),
                cancellationToken).ConfigureAwait(false);
            try
            {
                WorkspaceActionNativeFileSystem.RenameRelative(
                    temporary,
                    directory,
                    temporaryName,
                    directory,
                    fileName,
                    overwrite: false);
            }
            catch (IOException)
            {
                using var existing = WorkspaceActionNativeFileSystem.OpenRelativeFile(
                    directory,
                    fileName,
                    allowMissing: true,
                    write: false);
                if (existing is not null)
                {
                    return false;
                }
                throw;
            }
            WorkspaceActionNativeFileSystem.FlushDirectory(directory);
            published = true;
            return true;
        }
        finally
        {
            if (!published && temporary is not null)
            {
                try
                {
                    var identity = WorkspaceActionNativeFileSystem.GetIdentity(temporary);
                    WorkspaceActionNativeFileSystem.DeleteExact(directory, temporaryName, temporary, identity);
                    WorkspaceActionNativeFileSystem.FlushDirectory(directory);
                }
                catch when (cancellationToken.IsCancellationRequested)
                {
                    // Preserve the original cancellation while the exact bounded temporary remains recoverable.
                }
            }
            temporary?.Dispose();
        }
    }

    public async Task<byte[]> ReadAllBytesAsync(
        WorkspaceActionPrivateArtifactLockLease ownership,
        string fileName,
        long maximumBytes,
        string artifactName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        EnsureSimpleName(fileName);
        using var file = WorkspaceActionNativeFileSystem.OpenRelativeFile(
            ownership.DirectoryHandle,
            fileName,
            allowMissing: false,
            write: false)!;
        if (!WorkspaceActionNativeFileSystem.GetIdentity(file).SameMount(
                WorkspaceActionNativeFileSystem.GetIdentity(ownership.DirectoryHandle)))
        {
            throw new IOException("Workspace action private source refused a mounted file outside its retained directory.");
        }
        if (maximumBytes is < 0 or > int.MaxValue || RandomAccess.GetLength(file) > maximumBytes)
        {
            throw new FormatException($"{artifactName} exceeds its maximum private artifact size.");
        }
        return await WorkspaceActionNativeFileSystem.ReadAllBytesAsync(
            file,
            checked((int)maximumBytes),
            cancellationToken).ConfigureAwait(false);
    }

    public long GetFileLength(WorkspaceActionPrivateArtifactLockLease ownership, string fileName)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        EnsureSimpleName(fileName);
        using var file = WorkspaceActionNativeFileSystem.OpenRelativeFile(
            ownership.DirectoryHandle,
            fileName,
            allowMissing: false,
            write: false)!;
        if (!WorkspaceActionNativeFileSystem.GetIdentity(file).SameMount(
                WorkspaceActionNativeFileSystem.GetIdentity(ownership.DirectoryHandle)))
        {
            throw new IOException("Workspace action private source refused a mounted file outside its retained directory.");
        }
        return RandomAccess.GetLength(file);
    }

    private static void EnsureSimpleName(string name)
    {
        if (string.IsNullOrEmpty(name)
            || name is "." or ".."
            || name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0
            || name.Contains('\0'))
        {
            throw new ArgumentException("Workspace action private artifacts require one exact simple name.", nameof(name));
        }
    }
}
