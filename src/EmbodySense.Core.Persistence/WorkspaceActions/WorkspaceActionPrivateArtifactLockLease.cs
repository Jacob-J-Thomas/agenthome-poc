using Microsoft.Win32.SafeHandles;

namespace EmbodySense.Core.Persistence.WorkspaceActions;

/// <summary>Retains and revalidates one private artifact root and its exact cross-process ownership file.</summary>
internal sealed class WorkspaceActionPrivateArtifactLockLease : IDisposable
{
    private readonly SafeFileHandle _directory;
    private readonly WorkspaceActionNativeFileStamp _directoryIdentity;
    private readonly WorkspaceActionNativeFileStamp _lockIdentity;
    private readonly FileStream _ownership;
    private readonly string _root;
    private readonly string _workspaceRoot;
    private bool _disposed;

    public WorkspaceActionPrivateArtifactLockLease(
        SafeFileHandle directory,
        WorkspaceActionNativeFileStamp directoryIdentity,
        FileStream ownership,
        WorkspaceActionNativeFileStamp lockIdentity,
        string workspaceRoot,
        string root)
    {
        _directory = directory;
        _directoryIdentity = directoryIdentity;
        _ownership = ownership;
        _lockIdentity = lockIdentity;
        _workspaceRoot = workspaceRoot;
        _root = root;
    }

    internal SafeFileHandle DirectoryHandle
        => !_disposed
            ? _directory
            : throw new ObjectDisposedException(nameof(WorkspaceActionPrivateArtifactLockLease));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            ValidateNamedBindings();
        }
        finally
        {
            _ownership.Dispose();
            _directory.Dispose();
        }
    }

    private void ValidateNamedBindings()
    {
        using var currentDirectory = WorkspaceActionNativeFileSystem.OpenPrivateDirectoryUnderWorkspace(_workspaceRoot, _root);
        if (!WorkspaceActionNativeFileSystem.GetIdentity(currentDirectory).SameEntry(_directoryIdentity))
        {
            throw new IOException("Workspace action private root was substituted while its storage lock was retained.");
        }
        using var named = WorkspaceActionNativeFileSystem.OpenRelativeFile(
            _directory,
            ".custom-loop-mutations.lock",
            allowMissing: false,
            write: false)!;
        WorkspaceActionNativeFileSystem.RequireExactOpenedName(named, ".custom-loop-mutations.lock");
        if (!WorkspaceActionNativeFileSystem.GetIdentity(named).SameEntry(_lockIdentity))
        {
            throw new IOException("Workspace action private ownership file was substituted while its lock was retained.");
        }
    }
}
