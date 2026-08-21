using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace EmbodySense.Core.Persistence.WorkspaceActions;

/// <summary>Applies and verifies the restrictive policy for content-bearing workspace action internals.</summary>
internal static class WorkspaceActionPrivatePermissions
{
    public static void RequireDirectory(string workspaceRoot, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var handle = WorkspaceActionNativeFileSystem.OpenPrivateDirectoryUnderWorkspace(workspaceRoot, path);
        WorkspaceActionNativeFileSystem.RequirePrivateDirectoryPermissions(handle);
    }

    public static void RequireFile(string workspaceRoot, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var handle = WorkspaceActionNativeFileSystem.OpenPrivateFileUnderWorkspace(workspaceRoot, path);
        WorkspaceActionNativeFileSystem.RequirePrivateFilePermissions(handle);
    }

    [SupportedOSPlatform("windows")]
    public static void RequireDirectory(SafeFileHandle handle)
        => RequireWindowsHandle(handle, isDirectory: true);

    [SupportedOSPlatform("windows")]
    public static void RequireFile(SafeFileHandle handle)
        => RequireWindowsHandle(handle, isDirectory: false);

    [SupportedOSPlatform("windows")]
    private static void RequireWindowsHandle(SafeFileHandle handle, bool isDirectory)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var identity = WindowsIdentity.GetCurrent().User
            ?? throw new UnauthorizedAccessException("The current Windows identity cannot own private workspace action storage.");
        var security = new FileSecurity();
        security.SetOwner(identity);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            FileSystemRights.FullControl,
            isDirectory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        using var borrowedHandle = new SafeFileHandle(handle.DangerousGetHandle(), ownsHandle: false);
        using var stream = new FileStream(borrowedHandle, FileAccess.ReadWrite, bufferSize: 1, isAsync: false);
        FileSystemAclExtensions.SetAccessControl(stream, security);
        var retained = FileSystemAclExtensions.GetAccessControl(stream);
        var rules = retained.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        if (!identity.Equals(retained.GetOwner(typeof(SecurityIdentifier)))
            || !retained.AreAccessRulesProtected
            || rules.Length != 1
            || !IsCurrentUserFullControl(rules[0], identity))
        {
            throw new UnauthorizedAccessException("Private workspace action handle did not retain its exact current-user ACL.");
        }
        GC.KeepAlive(handle);
    }

    [SupportedOSPlatform("windows")]
    private static bool IsCurrentUserFullControl(FileSystemAccessRule rule, SecurityIdentifier identity)
        => identity.Equals(rule.IdentityReference)
            && !rule.IsInherited
            && rule.AccessControlType == AccessControlType.Allow
            && (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl;
}
