using Microsoft.Win32.SafeHandles;

namespace EmbodySense.Core.Persistence.WorkspaceActions;

/// <summary>Owns no-delete-share handles for every private directory ancestor used by one path-based Windows replacement.</summary>
internal sealed class WorkspaceActionRetainedPrivateDirectoryFence : IDisposable
{
    private readonly List<(SafeFileHandle Handle, WorkspaceActionNativeFileStamp Identity, string? Name)> _directories = [];
    private readonly string _rootPath;

    private WorkspaceActionRetainedPrivateDirectoryFence(string workspaceRoot, string path)
    {
        _rootPath = Path.GetFullPath(workspaceRoot);
        var segments = WorkspaceActionNativeFileSystem.PrivateRelativeSegments(_rootPath, path);
        var root = WorkspaceActionNativeFileSystem.OpenAbsoluteDirectory(_rootPath, denyDeleteSharing: true);
        try
        {
            var rootIdentity = WorkspaceActionNativeFileSystem.GetIdentity(root);
            _directories.Add((root, rootIdentity, null));
            var current = root;
            for (var index = 0; index < segments.Length; index++)
            {
                var next = WorkspaceActionNativeFileSystem.OpenRelativeDirectory(
                    current,
                    segments[index],
                    privateSecurityAccess: index == segments.Length - 1,
                    denyDeleteSharing: true);
                try
                {
                    WorkspaceActionNativeFileSystem.RequireExactOpenedName(next, segments[index]);
                    var identity = WorkspaceActionNativeFileSystem.GetIdentity(next);
                    if (!identity.SameMount(rootIdentity))
                    {
                        throw new IOException("Private workspace action replacement storage refused a mount or device crossing.");
                    }
                    _directories.Add((next, identity, segments[index]));
                    current = next;
                }
                catch
                {
                    next.Dispose();
                    throw;
                }
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public SafeFileHandle DirectoryHandle => _directories[^1].Handle;

    public static WorkspaceActionRetainedPrivateDirectoryFence Open(string workspaceRoot, string path)
        => new(workspaceRoot, path);

    public void Revalidate()
    {
        var rootIdentity = _directories[0].Identity;
        foreach (var (handle, identity, _) in _directories)
        {
            var current = WorkspaceActionNativeFileSystem.GetIdentity(handle);
            if (!current.SameEntry(identity)
                || !current.SameMount(rootIdentity)
                || !current.IsDirectory
                || current.IsReparsePoint)
            {
                throw new IOException("A retained private workspace action replacement directory was substituted.");
            }
        }

        var transient = new List<SafeFileHandle>(_directories.Count);
        try
        {
            var current = WorkspaceActionNativeFileSystem.OpenAbsoluteDirectory(_rootPath);
            transient.Add(current);
            if (!WorkspaceActionNativeFileSystem.GetIdentity(current).SameEntry(_directories[0].Identity))
            {
                throw new IOException("The private workspace action replacement root no longer resolves to its retained identity.");
            }
            for (var index = 1; index < _directories.Count; index++)
            {
                var next = WorkspaceActionNativeFileSystem.OpenRelativeDirectory(current, _directories[index].Name!);
                transient.Add(next);
                if (!WorkspaceActionNativeFileSystem.GetIdentity(next).SameEntry(_directories[index].Identity))
                {
                    throw new IOException("A private workspace action replacement ancestor no longer resolves to its retained identity.");
                }
                current = next;
            }
        }
        finally
        {
            for (var index = transient.Count - 1; index >= 0; index--)
            {
                transient[index].Dispose();
            }
        }
    }

    public void Dispose()
    {
        for (var index = _directories.Count - 1; index >= 0; index--)
        {
            _directories[index].Handle.Dispose();
        }
        _directories.Clear();
    }
}
