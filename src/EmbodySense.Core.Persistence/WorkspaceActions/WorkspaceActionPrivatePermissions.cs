using System.ComponentModel;
using System.Runtime.InteropServices;
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
        using var currentIdentity = WindowsIdentity.GetCurrent();
        var identity = currentIdentity.User
            ?? throw new UnauthorizedAccessException("The current Windows identity cannot own private workspace action storage.");
        FileSystemSecurity security = isDirectory ? new DirectorySecurity() : new FileSecurity();
        security.SetOwner(identity);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            FileSystemRights.FullControl,
            isDirectory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        SetRetainedHandleSecurity(handle, identity, security);
        using var borrowedHandle = new SafeFileHandle(handle.DangerousGetHandle(), ownsHandle: false);
        using var stream = new FileStream(borrowedHandle, FileAccess.ReadWrite, bufferSize: 1, isAsync: false);
        var retained = FileSystemAclExtensions.GetAccessControl(stream);
        var rules = retained.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        if (!identity.Equals(retained.GetOwner(typeof(SecurityIdentifier)))
            || !retained.AreAccessRulesProtected
            || rules.Length != 1
            || !IsCurrentUserFullControl(rules[0], identity, isDirectory))
        {
            throw new UnauthorizedAccessException("Private workspace action handle did not retain its exact current-user ACL.");
        }
        GC.KeepAlive(handle);
    }

    [SupportedOSPlatform("windows")]
    private static void SetRetainedHandleSecurity(
        SafeFileHandle handle,
        SecurityIdentifier identity,
        FileSystemSecurity security)
    {
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        var discretionaryAccessControlList = descriptor.DiscretionaryAcl
            ?? throw new UnauthorizedAccessException("Private workspace action storage requires an explicit DACL.");
        var ownerBytes = GetBinaryForm(identity);
        var accessControlListBytes = GetBinaryForm(discretionaryAccessControlList);
        var owner = GCHandle.Alloc(ownerBytes, GCHandleType.Pinned);
        var accessControlList = GCHandle.Alloc(accessControlListBytes, GCHandleType.Pinned);
        try
        {
            var status = SetSecurityInfo(
                handle,
                SeFileObject,
                OwnerSecurityInformation | DaclSecurityInformation | ProtectedDaclSecurityInformation,
                owner.AddrOfPinnedObject(),
                IntPtr.Zero,
                accessControlList.AddrOfPinnedObject(),
                IntPtr.Zero);
            if (status != 0)
            {
                throw new UnauthorizedAccessException(
                    "Private workspace action handle rejected its exact current-user ACL.",
                    new Win32Exception(unchecked((int)status)));
            }
        }
        finally
        {
            accessControlList.Free();
            owner.Free();
        }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] GetBinaryForm(GenericAcl accessControlList)
    {
        var bytes = new byte[accessControlList.BinaryLength];
        accessControlList.GetBinaryForm(bytes, 0);
        return bytes;
    }

    [SupportedOSPlatform("windows")]
    private static byte[] GetBinaryForm(SecurityIdentifier identity)
    {
        var bytes = new byte[identity.BinaryLength];
        identity.GetBinaryForm(bytes, 0);
        return bytes;
    }

    [SupportedOSPlatform("windows")]
    private static bool IsCurrentUserFullControl(
        FileSystemAccessRule rule,
        SecurityIdentifier identity,
        bool isDirectory)
        => identity.Equals(rule.IdentityReference)
            && !rule.IsInherited
            && rule.AccessControlType == AccessControlType.Allow
            && rule.FileSystemRights == FileSystemRights.FullControl
            && rule.InheritanceFlags == (isDirectory
                ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
                : InheritanceFlags.None)
            && rule.PropagationFlags == PropagationFlags.None;

    [DllImport("advapi32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    private static extern uint SetSecurityInfo(
        SafeFileHandle handle,
        int objectType,
        uint securityInformation,
        IntPtr owner,
        IntPtr group,
        IntPtr discretionaryAccessControlList,
        IntPtr systemAccessControlList);

    private const int SeFileObject = 1;
    private const uint OwnerSecurityInformation = 0x00000001;
    private const uint DaclSecurityInformation = 0x00000004;
    private const uint ProtectedDaclSecurityInformation = 0x80000000;
}
