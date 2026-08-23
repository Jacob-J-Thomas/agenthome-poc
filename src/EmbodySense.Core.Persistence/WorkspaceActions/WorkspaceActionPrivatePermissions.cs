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
        var retained = ReadRetainedHandleSecurity(handle, isDirectory);
        if (retained.Owner is null
            || !identity.Equals(retained.Owner)
            || !retained.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected)
            || !IsCurrentUserFullControl(retained.DiscretionaryAcl, identity, isDirectory))
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
    private static CommonSecurityDescriptor ReadRetainedHandleSecurity(
        SafeFileHandle handle,
        bool isDirectory)
    {
        var securityDescriptor = IntPtr.Zero;
        try
        {
            var status = GetSecurityInfo(
                handle,
                SeFileObject,
                OwnerSecurityInformation | DaclSecurityInformation,
                out _,
                out _,
                out _,
                out _,
                out securityDescriptor);
            if (status != 0)
            {
                throw new UnauthorizedAccessException(
                    "Private workspace action handle did not return its security descriptor.",
                    new Win32Exception(unchecked((int)status)));
            }
            if (securityDescriptor == IntPtr.Zero)
            {
                throw new UnauthorizedAccessException("Private workspace action handle returned an empty security descriptor.");
            }
            var length = GetSecurityDescriptorLength(securityDescriptor);
            if (length is 0 or > MaximumPrivateSecurityDescriptorBytes)
            {
                throw new UnauthorizedAccessException("Private workspace action handle returned an invalid security descriptor length.");
            }
            var binary = new byte[checked((int)length)];
            Marshal.Copy(securityDescriptor, binary, 0, binary.Length);
            return new CommonSecurityDescriptor(isDirectory, false, binary, 0);
        }
        catch (ArgumentException exception)
        {
            throw new UnauthorizedAccessException("Private workspace action handle returned an invalid security descriptor.", exception);
        }
        finally
        {
            if (securityDescriptor != IntPtr.Zero && LocalFree(securityDescriptor) != IntPtr.Zero)
            {
                throw new UnauthorizedAccessException(
                    "Private workspace action security descriptor could not be released.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsCurrentUserFullControl(
        DiscretionaryAcl? discretionaryAccessControlList,
        SecurityIdentifier identity,
        bool isDirectory)
    {
        if (discretionaryAccessControlList is null
            || discretionaryAccessControlList.Count != 1
            || discretionaryAccessControlList[0] is not CommonAce accessControlEntry)
        {
            return false;
        }
        var expectedAceFlags = isDirectory
            ? AceFlags.ObjectInherit | AceFlags.ContainerInherit
            : AceFlags.None;
        return identity.Equals(accessControlEntry.SecurityIdentifier)
            && accessControlEntry.AceQualifier == AceQualifier.AccessAllowed
            && accessControlEntry.AccessMask == (int)FileSystemRights.FullControl
            && !accessControlEntry.IsInherited
            && accessControlEntry.AceFlags == expectedAceFlags;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    private static extern uint GetSecurityInfo(
        SafeFileHandle handle,
        int objectType,
        uint securityInformation,
        out IntPtr owner,
        out IntPtr group,
        out IntPtr discretionaryAccessControlList,
        out IntPtr systemAccessControlList,
        out IntPtr securityDescriptor);

    [DllImport("advapi32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    private static extern uint GetSecurityDescriptorLength(IntPtr securityDescriptor);

    [DllImport("kernel32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    private static extern IntPtr LocalFree(IntPtr memory);

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
    private const uint MaximumPrivateSecurityDescriptorBytes = 128 * 1024;
}
