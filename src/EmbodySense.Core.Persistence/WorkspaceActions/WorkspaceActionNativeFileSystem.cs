using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.ContextualRoles;
using Microsoft.Win32.SafeHandles;

namespace EmbodySense.Core.Persistence.WorkspaceActions;

/// <summary>Supplies retained-handle, no-follow native primitives for exact workspace file commits.</summary>
internal static class WorkspaceActionNativeFileSystem
{
    private static readonly Encoding _strictUtf8 = new UTF8Encoding(false, true);

    public static SafeFileHandle OpenAbsoluteDirectory(string path, bool denyDeleteSharing = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (OperatingSystem.IsWindows())
        {
            var handle = CreateFile(
                Path.GetFullPath(path),
                GenericRead | GenericWrite | SynchronizeAccess,
                FileShareRead | FileShareWrite | (denyDeleteSharing ? 0 : FileShareDelete),
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint | FileFlagWriteThrough,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw NativeIOException("open absolute workspace directory", Marshal.GetLastPInvokeError());
            }
            return RetainValidated(handle, retained =>
            {
                RequireDirectory(retained, "workspace directory");
                RequireSupportedWindowsVolume(retained);
            });
        }

        RequireUnixPlatform();
        var descriptor = UnixOpen(Path.GetFullPath(path), UnixReadOnly | UnixDirectory | UnixNoFollow | UnixCloseOnExec);
        if (descriptor < 0)
        {
            throw NativeIOException("open absolute workspace directory", Marshal.GetLastPInvokeError());
        }
        var unix = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        return RetainValidated(unix, retained => RequireDirectory(retained, "workspace directory"));
    }

    public static SafeFileHandle OpenPrivateDirectoryUnderWorkspace(string workspaceRoot, string path)
        => OpenPrivateDirectoryUnderWorkspace(workspaceRoot, path, create: false);

    public static SafeFileHandle OpenOrCreatePrivateDirectoryUnderWorkspace(string workspaceRoot, string path)
        => OpenPrivateDirectoryUnderWorkspace(workspaceRoot, path, create: true);

    private static SafeFileHandle OpenPrivateDirectoryUnderWorkspace(string workspaceRoot, string path, bool create)
    {
        var segments = PrivateRelativeSegments(workspaceRoot, path);
        var current = OpenAbsoluteDirectory(workspaceRoot);
        var rootIdentity = GetIdentity(current);
        try
        {
            for (var index = 0; index < segments.Length; index++)
            {
                SafeFileHandle? next = null;
                try
                {
                    next = create
                        ? OpenOrCreateRelativeDirectory(
                            current,
                            segments[index],
                            privateSecurityAccess: index == segments.Length - 1)
                        : OpenRelativeDirectory(
                            current,
                            segments[index],
                            privateSecurityAccess: index == segments.Length - 1);
                    RequireExactOpenedName(next, segments[index]);
                    if (!GetIdentity(next).SameMount(rootIdentity))
                    {
                        throw new IOException("Private workspace action storage refused a mount or device crossing.");
                    }
                    current.Dispose();
                    current = next;
                    next = null;
                }
                finally
                {
                    next?.Dispose();
                }
            }
            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    public static SafeFileHandle OpenPrivateFileUnderWorkspace(string workspaceRoot, string path)
    {
        var segments = PrivateRelativeSegments(workspaceRoot, path);
        if (segments.Length < 2)
        {
            throw new IOException("Private workspace action files must remain beneath a retained private directory.");
        }
        var parentPath = Path.Combine([Path.GetFullPath(workspaceRoot), .. segments[..^1]]);
        using var parent = OpenPrivateDirectoryUnderWorkspace(workspaceRoot, parentPath);
        var parentIdentity = GetIdentity(parent);
        SafeFileHandle? file = null;
        try
        {
            file = OpenRelativeFileForUpdate(
                parent,
                segments[^1],
                allowMissing: false,
                create: false,
                shareForLocking: true)!;
            if (!GetIdentity(file).SameMount(parentIdentity))
            {
                throw new IOException("Private workspace action artifact refused a mounted file outside its retained directory.");
            }
            var retained = file;
            file = null;
            return retained;
        }
        finally
        {
            file?.Dispose();
        }
    }

    public static SafeFileHandle OpenRelativeDirectory(
        SafeFileHandle parent,
        string name,
        bool privateSecurityAccess = false,
        bool denyDeleteSharing = false)
    {
        EnsureSimpleName(name);
        if (OperatingSystem.IsWindows())
        {
            var windowsHandle = NtCreateRelative(
                parent,
                name,
                GenericRead | GenericWrite | (privateSecurityAccess ? PrivateSecurityAccess : 0) | SynchronizeAccess,
                NtOpen,
                NtDirectoryFile | NtSynchronousIoNonAlert | NtOpenReparsePoint | NtWriteThrough,
                allowMissing: false,
                FileShareRead | FileShareWrite | (denyDeleteSharing ? 0 : FileShareDelete));
            return RetainValidated(windowsHandle!, retained => RequireDirectory(retained, "workspace ancestor"));
        }

        var descriptor = UnixOpenAt(parent.DangerousGetHandle().ToInt32(), name, UnixReadOnly | UnixDirectory | UnixNoFollow | UnixCloseOnExec, 0);
        if (descriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw error is UnixNotDirectory || error == UnixSymbolicLinkLoop
                ? new IOException("Workspace target traversal refused a symbolic-link or non-directory ancestor.")
                : NativeIOException("openat workspace ancestor", error);
        }
        var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        return RetainValidated(handle, retained => RequireDirectory(retained, "workspace ancestor"));
    }

    internal static string[] PrivateRelativeSegments(string workspaceRoot, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var root = Path.GetFullPath(workspaceRoot);
        var target = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(root, target);
        if (Path.IsPathRooted(relative) || relative is "." or "..")
        {
            throw new IOException("Private workspace action storage escaped its retained workspace root.");
        }
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            throw new IOException("Private workspace action storage has a noncanonical retained path.");
        }
        return segments;
    }

    public static SafeFileHandle OpenOrCreateRelativeDirectory(
        SafeFileHandle parent,
        string name,
        bool privateSecurityAccess = false)
    {
        EnsureSimpleName(name);
        SafeFileHandle handle;
        if (OperatingSystem.IsWindows())
        {
            handle = NtCreateRelative(
                parent,
                name,
                GenericRead | GenericWrite | DeleteAccess | (privateSecurityAccess ? PrivateSecurityAccess : 0) | SynchronizeAccess,
                NtOpenIf,
                NtDirectoryFile | NtSynchronousIoNonAlert | NtOpenReparsePoint | NtWriteThrough,
                allowMissing: false,
                FileShareRead | FileShareWrite | FileShareDelete)!;
        }
        else
        {
            var parentDescriptor = parent.DangerousGetHandle().ToInt32();
            var descriptor = UnixOpenAt(parentDescriptor, name, UnixReadOnly | UnixDirectory | UnixNoFollow | UnixCloseOnExec, 0);
            if (descriptor < 0 && Marshal.GetLastPInvokeError() == UnixNoEntry)
            {
                var createResult = UnixMkdirAt(parentDescriptor, name, PermissionUserReadWriteExecute);
                var createError = Marshal.GetLastPInvokeError();
                if (createResult != 0 && createError != UnixAlreadyExists)
                {
                    throw NativeIOException("mkdirat private workspace action directory", createError);
                }
                descriptor = UnixOpenAt(parentDescriptor, name, UnixReadOnly | UnixDirectory | UnixNoFollow | UnixCloseOnExec, 0);
            }
            if (descriptor < 0)
            {
                throw NativeIOException("openat private workspace action directory", Marshal.GetLastPInvokeError());
            }
            handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        }

        return RetainValidated(handle, retained =>
        {
            RequireDirectory(retained, "workspace action private directory");
            RequireExactOpenedName(retained, name);
        });
    }

    public static SafeFileHandle? OpenRelativeFile(
        SafeFileHandle parent,
        string name,
        bool allowMissing,
        bool write,
        bool denyDeleteSharing = false,
        bool denyWriteSharing = false,
        bool privateSecurityAccess = false,
        bool allowMultipleLinks = false)
    {
        EnsureSimpleName(name);
        if (OperatingSystem.IsWindows())
        {
            var access = GenericRead
                | SynchronizeAccess
                | (write ? DeleteAccess : 0)
                | (privateSecurityAccess ? PrivateSecurityAccess : 0);
            var shareAccess = FileShareRead
                | (denyWriteSharing ? 0 : FileShareWrite)
                | (denyDeleteSharing ? 0 : FileShareDelete);
            var handle = NtCreateRelative(
                parent,
                name,
                access,
                NtOpen,
                NtNonDirectoryFile | NtSynchronousIoNonAlert | NtOpenReparsePoint | (write ? NtWriteThrough : 0),
                allowMissing,
                shareAccess);
            if (handle is not null)
            {
                return RetainValidated(handle, retained => RequireRegularFile(retained, "workspace target", requireSingleLink: !allowMultipleLinks));
            }
            return null;
        }

        var flags = UnixReadOnly | UnixNoFollow | UnixCloseOnExec | UnixNonBlocking;
        var descriptor = UnixOpenAt(parent.DangerousGetHandle().ToInt32(), name, flags, 0);
        if (descriptor >= 0)
        {
            var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
            return RetainValidated(handle, retained => RequireRegularFile(retained, "workspace target", requireSingleLink: !allowMultipleLinks));
        }
        var error = Marshal.GetLastPInvokeError();
        if (allowMissing && error == UnixNoEntry)
        {
            return null;
        }
        throw error is UnixNotDirectory || error == UnixSymbolicLinkLoop
            ? new IOException("Workspace target refused a symbolic link or entry-kind substitution.")
            : NativeIOException("openat workspace target", error);
    }

    public static void RequireExactOpenedName(SafeFileHandle handle, string expectedName)
    {
        EnsureSimpleName(expectedName);
        var finalPath = ReadFinalPath(handle);
        var end = finalPath.Length;
        while (end > 0 && finalPath[end - 1] is '/' or '\\')
        {
            end--;
        }
        if (end == 0)
        {
            throw new IOException("Workspace exact-name proof returned an empty native path.");
        }
        var separator = finalPath.LastIndexOfAny(['/', '\\'], end - 1, end);
        var actualName = finalPath[(separator + 1)..end];
        if (!string.Equals(actualName, expectedName, StringComparison.Ordinal))
        {
            throw new IOException("Workspace target traversal refused a host-equivalent noncanonical name alias.");
        }
    }

    public static SafeFileHandle CreateRelativeFile(
        SafeFileHandle parent,
        string name,
        bool privateSecurityAccess = false)
    {
        EnsureSimpleName(name);
        if (OperatingSystem.IsWindows())
        {
            var windowsHandle = NtCreateRelative(
                parent,
                name,
                GenericRead | GenericWrite | DeleteAccess | (privateSecurityAccess ? PrivateSecurityAccess : 0) | SynchronizeAccess,
                NtCreate,
                NtNonDirectoryFile | NtSynchronousIoNonAlert | NtOpenReparsePoint | NtWriteThrough,
                allowMissing: false,
                FileShareRead | FileShareWrite | FileShareDelete);
            return RetainValidated(
                windowsHandle!,
                retained => RequireRegularFile(retained, "workspace action private stage"));
        }

        var descriptor = UnixOpenAt(
            parent.DangerousGetHandle().ToInt32(),
            name,
            UnixReadWrite | UnixNoFollow | UnixCloseOnExec | UnixCreate | UnixExclusive,
            PermissionUserReadWrite);
        if (descriptor < 0)
        {
            throw NativeIOException("openat exclusive workspace action stage", Marshal.GetLastPInvokeError());
        }
        if (UnixFchmod(descriptor, PermissionUserReadWrite) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            _ = UnixClose(descriptor);
            throw NativeIOException("fchmod workspace action stage", error);
        }
        var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        return RetainValidated(handle, retained => RequireRegularFile(retained, "workspace action private stage"));
    }

    public static SafeFileHandle? OpenRelativeFileForUpdate(
        SafeFileHandle parent,
        string name,
        bool allowMissing,
        bool create,
        bool shareForLocking = false,
        bool denyDeleteSharing = false,
        bool requireDeleteAccess = true)
    {
        EnsureSimpleName(name);
        SafeFileHandle? handle;
        if (OperatingSystem.IsWindows())
        {
            handle = NtCreateRelative(
                parent,
                name,
                GenericRead | GenericWrite | (requireDeleteAccess ? DeleteAccess : 0) | PrivateSecurityAccess | SynchronizeAccess,
                create ? NtOpenIf : NtOpen,
                NtNonDirectoryFile | NtSynchronousIoNonAlert | NtOpenReparsePoint | NtWriteThrough,
                allowMissing,
                shareForLocking
                    ? FileShareRead | FileShareWrite | (denyDeleteSharing ? 0 : FileShareDelete)
                    : FileShareRead);
        }
        else
        {
            var flags = UnixReadWrite | UnixNoFollow | UnixCloseOnExec | (create ? UnixCreate : 0);
            var descriptor = UnixOpenAt(parent.DangerousGetHandle().ToInt32(), name, flags, PermissionUserReadWrite);
            if (descriptor < 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (allowMissing && error == UnixNoEntry)
                {
                    return null;
                }
                throw error is UnixNotDirectory || error == UnixSymbolicLinkLoop
                    ? new IOException("Workspace private artifact refused a symbolic link or entry-kind substitution.")
                    : NativeIOException("openat private workspace action artifact", error);
            }
            handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        }
        if (handle is not null)
        {
            return RetainValidated(handle, retained =>
            {
                RequireRegularFile(retained, "workspace action private artifact");
                RequireExactOpenedName(retained, name);
                RequirePrivateFilePermissions(retained);
            });
        }
        return null;
    }

    public static IReadOnlyList<string> EnumerateRelativeNames(SafeFileHandle directory, int maximumEntries)
    {
        ArgumentNullException.ThrowIfNull(directory);
        if (maximumEntries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        }
        RequireDirectory(directory, "workspace action private directory");
        return OperatingSystem.IsWindows()
            ? EnumerateWindowsRelativeNames(directory, maximumEntries)
            : EnumerateUnixRelativeNames(directory, maximumEntries);
    }

    public static SafeFileHandle Duplicate(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (OperatingSystem.IsWindows())
        {
            var process = GetCurrentProcess();
            if (!DuplicateHandle(
                    process,
                    handle,
                    process,
                    out var duplicate,
                    0,
                    inheritHandle: false,
                    DuplicateSameAccess))
            {
                throw NativeIOException("DuplicateHandle workspace action retained handle", Marshal.GetLastPInvokeError());
            }
            return duplicate;
        }
        RequireUnixPlatform();
        var descriptor = DuplicateUnixCloseOnExec(handle.DangerousGetHandle().ToInt32());
        if (descriptor < 0)
        {
            throw NativeIOException("dup workspace action retained handle", Marshal.GetLastPInvokeError());
        }
        return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
    }

    private static IReadOnlyList<string> EnumerateWindowsRelativeNames(SafeFileHandle directory, int maximumEntries)
    {
        const int BufferBytes = 64 * 1024;
        var buffer = Marshal.AllocHGlobal(BufferBytes);
        try
        {
            var names = new List<string>(Math.Min(maximumEntries, 64));
            var restartScan = true;
            while (names.Count < maximumEntries)
            {
                var status = NtQueryDirectoryFile(
                    directory,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    out var ioStatus,
                    buffer,
                    BufferBytes,
                    FileDirectoryInformation,
                    0,
                    IntPtr.Zero,
                    restartScan ? (byte)1 : (byte)0);
                restartScan = false;
                if (unchecked((uint)status) == StatusNoMoreFiles)
                {
                    break;
                }
                if (status < 0)
                {
                    throw new IOException($"NtQueryDirectoryFile workspace action enumeration failed closed with NTSTATUS 0x{unchecked((uint)status):x8}.");
                }
                var returnedBytes = ioStatus.Information.ToInt64();
                if (returnedBytes is <= 0 or > BufferBytes)
                {
                    throw new FormatException("Windows returned an invalid bounded workspace action directory buffer length.");
                }
                var offset = 0;
                while (true)
                {
                    if (returnedBytes - offset < WindowsFileDirectoryInformationNameOffset)
                    {
                        throw new FormatException("Windows returned a truncated workspace action directory entry.");
                    }
                    var nextOffset = unchecked((uint)Marshal.ReadInt32(buffer, offset));
                    var nameBytes = unchecked((uint)Marshal.ReadInt32(buffer, offset + WindowsFileDirectoryInformationNameLengthOffset));
                    if (nameBytes is 0 or > WindowsMaximumFileNameBytes
                        || (nameBytes & 1) != 0
                        || returnedBytes - offset - WindowsFileDirectoryInformationNameOffset < nameBytes)
                    {
                        throw new FormatException("Windows returned an invalid workspace action directory entry name length.");
                    }
                    var name = Marshal.PtrToStringUni(
                        buffer + offset + WindowsFileDirectoryInformationNameOffset,
                        checked((int)nameBytes / 2))
                        ?? throw new FormatException("Windows returned an invalid workspace action directory entry name.");
                    if (name is not "." and not "..")
                    {
                        names.Add(name);
                        if (names.Count >= maximumEntries)
                        {
                            break;
                        }
                    }
                    if (nextOffset == 0)
                    {
                        break;
                    }
                    if (nextOffset < WindowsFileDirectoryInformationNameOffset
                        || (nextOffset & 7) != 0
                        || offset + nextOffset >= returnedBytes)
                    {
                        throw new FormatException("Windows returned an invalid workspace action directory entry offset.");
                    }
                    offset = checked(offset + (int)nextOffset);
                }
            }
            return names;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IReadOnlyList<string> EnumerateUnixRelativeNames(SafeFileHandle directory, int maximumEntries)
    {
        RequireUnixPlatform();
        var duplicate = DuplicateUnixCloseOnExec(directory.DangerousGetHandle().ToInt32());
        if (duplicate < 0)
        {
            throw NativeIOException("dup workspace action directory", Marshal.GetLastPInvokeError());
        }
        var stream = UnixFdOpenDirectory(duplicate);
        if (stream == IntPtr.Zero)
        {
            var error = Marshal.GetLastPInvokeError();
            _ = UnixClose(duplicate);
            throw NativeIOException("fdopendir workspace action directory", error);
        }
        try
        {
            UnixRewindDirectory(stream);
            var names = new List<string>(Math.Min(maximumEntries, 64));
            while (names.Count < maximumEntries)
            {
                Marshal.SetLastPInvokeError(0);
                var entry = UnixReadDirectory(stream);
                if (entry == IntPtr.Zero)
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error != 0)
                    {
                        throw NativeIOException("readdir workspace action directory", error);
                    }
                    break;
                }
                var recordLength = unchecked((ushort)Marshal.ReadInt16(entry, UnixDirectoryRecordLengthOffset));
                var nameOffset = OperatingSystem.IsMacOS() ? MacDirectoryNameOffset : LinuxDirectoryNameOffset;
                if (recordLength <= nameOffset || recordLength > UnixMaximumDirectoryRecordBytes)
                {
                    throw new FormatException("Unix returned an invalid workspace action directory entry length.");
                }
                var availableNameBytes = recordLength - nameOffset;
                var nameLength = 0;
                while (nameLength < availableNameBytes && Marshal.ReadByte(entry, nameOffset + nameLength) != 0)
                {
                    nameLength++;
                }
                if (nameLength == availableNameBytes || nameLength > UnixMaximumFileNameBytes)
                {
                    throw new FormatException("Unix returned an unterminated or oversized workspace action directory entry name.");
                }
                var encoded = new byte[nameLength];
                Marshal.Copy(entry + nameOffset, encoded, 0, nameLength);
                string name;
                try
                {
                    name = _strictUtf8.GetString(encoded);
                }
                catch (DecoderFallbackException exception)
                {
                    throw new FormatException("Unix returned a workspace action directory entry name that is not valid UTF-8.", exception);
                }
                if (name is not "." and not "..")
                {
                    names.Add(name);
                }
            }
            return names;
        }
        finally
        {
            if (UnixCloseDirectory(stream) != 0)
            {
                throw NativeIOException("closedir workspace action directory", Marshal.GetLastPInvokeError());
            }
        }
    }

    public static void RequirePrivateDirectoryPermissions(SafeFileHandle handle)
    {
        RequireDirectory(handle, "workspace action private directory");
        if (OperatingSystem.IsWindows())
        {
            WorkspaceActionPrivatePermissions.RequireDirectory(handle);
            return;
        }
        if (UnixFchmod(handle.DangerousGetHandle().ToInt32(), PermissionUserReadWriteExecute) != 0)
        {
            throw NativeIOException("fchmod private workspace action directory", Marshal.GetLastPInvokeError());
        }
        if (OperatingSystem.IsLinux())
        {
            RemoveLinuxAccessControl(handle.DangerousGetHandle().ToInt32(), includeDefault: true);
        }
        RemoveMacExtendedAccessControl(handle);
        RequireDirectory(handle, "workspace action private directory");
        if ((GetIdentity(handle).Mode & UnixPermissionMask) != PermissionUserReadWriteExecute)
        {
            throw new UnauthorizedAccessException("Private workspace action directory did not retain mode 0700.");
        }
    }

    public static void RequirePrivateFilePermissions(SafeFileHandle handle)
    {
        RequireRegularFile(handle, "workspace action private artifact");
        if (OperatingSystem.IsWindows())
        {
            WorkspaceActionPrivatePermissions.RequireFile(handle);
            return;
        }
        if (UnixFchmod(handle.DangerousGetHandle().ToInt32(), PermissionUserReadWrite) != 0)
        {
            throw NativeIOException("fchmod private workspace action artifact", Marshal.GetLastPInvokeError());
        }
        if (OperatingSystem.IsLinux())
        {
            RemoveLinuxAccessControl(handle.DangerousGetHandle().ToInt32(), includeDefault: false);
        }
        RemoveMacExtendedAccessControl(handle);
        RequireRegularFile(handle, "workspace action private artifact");
        if ((GetIdentity(handle).Mode & UnixPermissionMask) != PermissionUserReadWrite)
        {
            throw new UnauthorizedAccessException("Private workspace action artifact did not retain mode 0600.");
        }
    }

    public static WorkspaceActionNativeFileStamp GetIdentity(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (OperatingSystem.IsWindows())
        {
            if (!GetFileInformationByHandle(handle, out var information))
            {
                throw NativeIOException("GetFileInformationByHandle", Marshal.GetLastPInvokeError());
            }
            if (!GetFileInformationByHandleEx(
                    handle,
                    FileIdInfo,
                    out CapabilityCatalogFileIdInfo fileId,
                    (uint)Marshal.SizeOf<CapabilityCatalogFileIdInfo>()))
            {
                throw NativeIOException("GetFileInformationByHandleEx FileIdInfo", Marshal.GetLastPInvokeError());
            }
            var creationTime = ((ulong)information.CreationTime.High << 32) | information.CreationTime.Low;
            if (creationTime == 0)
            {
                throw new PlatformNotSupportedException("Windows workspace actions require a nonzero NTFS creation-time lifetime discriminator.");
            }
            var isDirectory = (information.FileAttributes & FileAttributeDirectory) != 0;
            var isReparse = (information.FileAttributes & FileAttributeReparsePoint) != 0;
            return new WorkspaceActionNativeFileStamp(
                fileId.VolumeSerialNumber,
                fileId.VolumeSerialNumber,
                fileId.FileId.ToString("N"),
                $"creation-{creationTime:x16}",
                information.NumberOfLinks,
                0,
                0,
                0,
                isDirectory,
                !isDirectory && !isReparse,
                isReparse);
        }
        if (OperatingSystem.IsLinux())
        {
            if (LinuxStatx(
                    handle.DangerousGetHandle().ToInt32(),
                    string.Empty,
                    LinuxAtEmptyPath | LinuxAtNoAutomount,
                    LinuxStatxBasicStats | LinuxStatxBirthTime | LinuxStatxMountIdUnique,
                    out var statx) != 0)
            {
                throw NativeIOException("statx workspace action handle", Marshal.GetLastPInvokeError());
            }
            if ((statx.Mask & (LinuxStatxBirthTime | LinuxStatxMountIdUnique)) != (LinuxStatxBirthTime | LinuxStatxMountIdUnique)
                || statx.BirthTimeSeconds == 0 && statx.BirthTimeNanoseconds == 0)
            {
                throw new PlatformNotSupportedException("Linux workspace actions require statx unique-mount and birth-time lifetime identity support.");
            }
            var inodeGeneration = ReadLinuxInodeGeneration(handle);
            var device = ((ulong)statx.DeviceMajor << 32) | statx.DeviceMinor;
            return new WorkspaceActionNativeFileStamp(
                device,
                statx.MountId,
                statx.Inode.ToString("x16", System.Globalization.CultureInfo.InvariantCulture),
                $"generation-{inodeGeneration:x16}-birth-{statx.BirthTimeSeconds:x16}-{statx.BirthTimeNanoseconds:x8}",
                statx.LinkCount,
                statx.Mode,
                statx.OwnerId,
                statx.GroupId,
                (statx.Mode & UnixFileTypeMask) == UnixDirectoryType,
                (statx.Mode & UnixFileTypeMask) == UnixRegularFileType,
                (statx.Mode & UnixFileTypeMask) == UnixSymbolicLinkType);
        }

        RequireUnixPlatform();
        if (MacFstat(handle.DangerousGetHandle().ToInt32(), out var mac) != 0)
        {
            throw NativeIOException("fstat workspace action handle", Marshal.GetLastPInvokeError());
        }
        var macLifetime = CapabilityCatalogNativeFileSystem.GetPhysicalIdentityMaterial(handle);
        return new WorkspaceActionNativeFileStamp(
            mac.Device,
            mac.Device,
            mac.Inode.ToString("x16", System.Globalization.CultureInfo.InvariantCulture),
            macLifetime,
            mac.LinkCount,
            mac.Mode,
            mac.UserId,
            mac.GroupId,
            (mac.Mode & UnixFileTypeMask) == UnixDirectoryType,
            (mac.Mode & UnixFileTypeMask) == UnixRegularFileType,
            (mac.Mode & UnixFileTypeMask) == UnixSymbolicLinkType);
    }

    public static void RequireDirectory(SafeFileHandle handle, string description)
    {
        var identity = GetIdentity(handle);
        if (!identity.IsDirectory || identity.IsReparsePoint || identity.LinkCount == 0)
        {
            throw new IOException($"The retained {description} is not one no-follow directory.");
        }
    }

    private static void RequireSupportedWindowsVolume(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var fileSystemName = new StringBuilder(32);
        if (!GetVolumeInformationByHandle(handle, null, 0, out _, out _, out _, fileSystemName, fileSystemName.Capacity))
        {
            throw NativeIOException("GetVolumeInformationByHandleW workspace volume", Marshal.GetLastPInvokeError());
        }
        if (!string.Equals(fileSystemName.ToString(), "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            throw new PlatformNotSupportedException("Windows workspace actions require NTFS exact file identity and fail closed on other filesystems.");
        }
    }

    private static string ReadFinalPath(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
        {
            var buffer = new StringBuilder(MaximumNativePathBytes);
            var windowsLength = GetFinalPathNameByHandle(handle, buffer, buffer.Capacity, 0);
            if (windowsLength == 0 || windowsLength >= buffer.Capacity)
            {
                throw NativeIOException("GetFinalPathNameByHandleW workspace exact-name proof", Marshal.GetLastPInvokeError());
            }
            return buffer.ToString();
        }

        var bytes = new byte[MaximumNativePathBytes];
        int length;
        if (OperatingSystem.IsLinux())
        {
            var descriptorPath = $"/proc/self/fd/{handle.DangerousGetHandle().ToInt32()}";
            var read = UnixReadLink(descriptorPath, bytes, (nuint)bytes.Length);
            if (read <= 0 || read >= bytes.Length)
            {
                throw NativeIOException("readlink workspace exact-name proof", Marshal.GetLastPInvokeError());
            }
            length = checked((int)read);
        }
        else if (OperatingSystem.IsMacOS())
        {
            var nativeInfo = Marshal.AllocHGlobal(MacVnodeFdInfoWithPathBytes);
            try
            {
                var read = MacProcPidFdInfo(
                    Environment.ProcessId,
                    handle.DangerousGetHandle().ToInt32(),
                    MacProcessFdVnodePathInfo,
                    nativeInfo,
                    MacVnodeFdInfoWithPathBytes);
                if (read < MacVnodeFdInfoWithPathBytes)
                {
                    throw NativeIOException("proc_pidfdinfo workspace exact-name proof", Marshal.GetLastPInvokeError());
                }
                Marshal.Copy(IntPtr.Add(nativeInfo, MacVnodePathOffset), bytes, 0, MacMaximumPathBytes);
            }
            finally
            {
                Marshal.FreeHGlobal(nativeInfo);
            }
            length = Array.IndexOf(bytes, (byte)0);
            if (length <= 0)
            {
                throw new IOException("proc_pidfdinfo workspace exact-name proof returned an empty path.");
            }
        }
        else
        {
            throw new PlatformNotSupportedException("Governed workspace native actions support Windows, Linux, and macOS.");
        }

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes, 0, length);
        }
        catch (DecoderFallbackException exception)
        {
            throw new IOException("Workspace exact-name proof returned a non-UTF-8 native path.", exception);
        }
    }

    private static ulong ReadLinuxInodeGeneration(SafeFileHandle handle)
    {
        if (IntPtr.Size == 8)
        {
            if (LinuxIoctlGetVersion64(handle.DangerousGetHandle().ToInt32(), LinuxGetVersion64, out var generation) != 0
                || generation <= 0)
            {
                throw new PlatformNotSupportedException("Linux workspace actions require a positive filesystem inode generation.");
            }
            return checked((ulong)generation);
        }
        if (LinuxIoctlGetVersion32(handle.DangerousGetHandle().ToInt32(), LinuxGetVersion32, out var generation32) != 0
            || generation32 <= 0)
        {
            throw new PlatformNotSupportedException("Linux workspace actions require a positive filesystem inode generation.");
        }
        return checked((uint)generation32);
    }

    public static void RequireRegularFile(SafeFileHandle handle, string description)
        => RequireRegularFile(handle, description, requireSingleLink: true);
    private static void RequireRegularFile(SafeFileHandle handle, string description, bool requireSingleLink)
    {
        var identity = GetIdentity(handle);
        if (!identity.IsRegularFile
            || identity.IsDirectory
            || identity.IsReparsePoint
            || (requireSingleLink && identity.LinkCount != 1))
        {
            throw new IOException($"The retained {description} is not one {(requireSingleLink ? "single-link " : string.Empty)}regular file.");
        }
    }

    public static async Task<byte[]> ReadAllBytesAsync(
        SafeFileHandle handle,
        int maximumBytes,
        CancellationToken cancellationToken,
        bool requireSingleLink = true)
    {
        RequireRegularFile(handle, "workspace target", requireSingleLink);
        var length = RandomAccess.GetLength(handle);
        if (length is < 0 || length > maximumBytes)
        {
            throw new IOException("The workspace target exceeds the admitted before-image byte bound.");
        }
        var bytes = new byte[checked((int)length)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await RandomAccess.ReadAsync(handle, bytes.AsMemory(offset), offset, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("The workspace target changed length during retained-handle evidence capture.");
            }
            offset += read;
        }
        if (RandomAccess.GetLength(handle) != length)
        {
            throw new IOException("The workspace target changed length during retained-handle evidence capture.");
        }
        RequireRegularFile(handle, "workspace target", requireSingleLink);
        return bytes;
    }

    public static async Task WriteAllBytesAsync(SafeFileHandle handle, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        RequireRegularFile(handle, "workspace action stage");
        RandomAccess.SetLength(handle, bytes.Length);
        var offset = 0;
        while (offset < bytes.Length)
        {
            await RandomAccess.WriteAsync(handle, bytes[offset..], offset, cancellationToken).ConfigureAwait(false);
            offset = bytes.Length;
        }
        FlushFile(handle);
        RequireRegularFile(handle, "workspace action stage");
    }

    public static void PreserveReplacementMetadata(SafeFileHandle source, SafeFileHandle stage)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows replacement metadata is authenticated after ReplaceFileW publication.");
        }
        var sourceIdentity = GetIdentity(source);
        var sourceDescriptor = source.DangerousGetHandle().ToInt32();
        var stageDescriptor = stage.DangerousGetHandle().ToInt32();
        var mode = checked((int)(sourceIdentity.Mode & UnixPermissionMask));
        if (OperatingSystem.IsMacOS())
        {
            if (MacCopyFile(sourceDescriptor, stageDescriptor, IntPtr.Zero, MacCopyFileSecurity) != 0)
            {
                throw NativeIOException("fcopyfile preserve workspace target security", Marshal.GetLastPInvokeError());
            }
            return;
        }
        if (UnixFchown(stageDescriptor, sourceIdentity.OwnerId, sourceIdentity.GroupId) != 0)
        {
            throw NativeIOException("fchown preserve workspace target owner and group", Marshal.GetLastPInvokeError());
        }
        if (UnixFchmod(stageDescriptor, mode) != 0)
        {
            throw NativeIOException("fchmod preserve workspace target mode", Marshal.GetLastPInvokeError());
        }
        var accessAcl = ReadLinuxAccessAcl(sourceDescriptor);
        if (accessAcl is not null)
        {
            WriteLinuxAccessAcl(stageDescriptor, accessAcl);
        }
        else
        {
            RemoveLinuxAccessControl(stageDescriptor, includeDefault: false);
        }
    }

    public static void RequireReplacementMetadata(SafeFileHandle source, SafeFileHandle replacement)
    {
        if (OperatingSystem.IsWindows())
        {
            RequireWindowsReplacementMetadata(source, replacement, expectedProtectedDiscretionaryAccessControlList: null);
            return;
        }
        var sourceIdentity = GetIdentity(source);
        var replacementIdentity = GetIdentity(replacement);
        if ((sourceIdentity.Mode & UnixPermissionMask) != (replacementIdentity.Mode & UnixPermissionMask)
            || sourceIdentity.OwnerId != replacementIdentity.OwnerId
            || sourceIdentity.GroupId != replacementIdentity.GroupId
            || !ReadAccessControlEvidence(source).AsSpan().SequenceEqual(ReadAccessControlEvidence(replacement)))
        {
            throw new IOException("The published workspace replacement did not preserve the exact Unix owner, group, mode, and access-control evidence.");
        }
    }

    public static void RemoveMacExtendedAccessControl(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }
        var empty = MacAclInit(0);
        if (empty == IntPtr.Zero)
        {
            throw NativeIOException("acl_init private workspace action access control", Marshal.GetLastPInvokeError());
        }
        try
        {
            if (MacAclSetFd(handle.DangerousGetHandle().ToInt32(), empty, MacExtendedAclType) != 0)
            {
                throw NativeIOException("acl_set_fd_np private workspace action access control", Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            _ = MacAclFree(empty);
        }
        if (ReadMacAccessAcl(handle.DangerousGetHandle().ToInt32()).Length != 0)
        {
            throw new UnauthorizedAccessException("Private workspace action storage retained an extended access-control entry.");
        }
    }

    private static byte[] ReadAccessControlEvidence(SafeFileHandle handle)
    {
        var descriptor = handle.DangerousGetHandle().ToInt32();
        return OperatingSystem.IsLinux()
            ? ReadLinuxAccessAcl(descriptor) ?? []
            : ReadMacAccessAcl(descriptor);
    }

    private static byte[]? ReadLinuxAccessAcl(int descriptor)
    {
        var length = LinuxFGetXattr(descriptor, LinuxAccessAclName, IntPtr.Zero, 0);
        if (length < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == LinuxNoData)
            {
                return null;
            }
            throw NativeIOException("fgetxattr inspect workspace target access ACL", error);
        }
        if (length > MaximumAccessControlBytes)
        {
            throw new IOException("The workspace target access ACL exceeds the bounded native metadata envelope.");
        }
        if (length == 0)
        {
            return [];
        }
        var buffer = Marshal.AllocHGlobal(checked((int)length));
        try
        {
            var read = LinuxFGetXattr(descriptor, LinuxAccessAclName, buffer, checked((nuint)length));
            if (read != length)
            {
                throw read < 0
                    ? NativeIOException("fgetxattr read workspace target access ACL", Marshal.GetLastPInvokeError())
                    : new IOException("The workspace target access ACL changed during retained-handle capture.");
            }
            var bytes = new byte[checked((int)length)];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void WriteLinuxAccessAcl(int descriptor, byte[] value)
    {
        var buffer = Marshal.AllocHGlobal(value.Length);
        try
        {
            Marshal.Copy(value, 0, buffer, value.Length);
            if (LinuxFSetXattr(descriptor, LinuxAccessAclName, buffer, checked((nuint)value.Length), 0) != 0)
            {
                throw NativeIOException("fsetxattr preserve workspace target access ACL", Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void RemoveLinuxAccessControl(int descriptor, bool includeDefault)
    {
        RemoveLinuxExtendedAttribute(descriptor, LinuxAccessAclName);
        if (includeDefault)
        {
            RemoveLinuxExtendedAttribute(descriptor, LinuxDefaultAclName);
        }
    }

    private static void RemoveLinuxExtendedAttribute(int descriptor, string name)
    {
        if (LinuxFRemoveXattr(descriptor, name) != 0)
        {
            var removeError = Marshal.GetLastPInvokeError();
            if (removeError != LinuxNoData)
            {
                throw NativeIOException("fremovexattr clear private workspace action ACL", removeError);
            }
        }
        if (LinuxFGetXattr(descriptor, name, IntPtr.Zero, 0) >= 0)
        {
            throw new UnauthorizedAccessException("Private workspace action storage retained an extended POSIX ACL.");
        }
        var verifyError = Marshal.GetLastPInvokeError();
        if (verifyError != LinuxNoData)
        {
            throw NativeIOException("fgetxattr verify private workspace action ACL removal", verifyError);
        }
    }

    private static byte[] ReadMacAccessAcl(int descriptor)
    {
        var acl = MacAclGetFd(descriptor);
        if (acl == IntPtr.Zero)
        {
            var error = Marshal.GetLastPInvokeError();
            return error == UnixNoEntry
                ? []
                : throw NativeIOException("acl_get_fd inspect workspace target access ACL", error);
        }
        try
        {
            var length = MacAclSize(acl);
            if (length is < 0 or > MaximumAccessControlBytes)
            {
                throw new IOException("The workspace target access ACL exceeds the bounded native metadata envelope.");
            }
            if (length == 0)
            {
                return [];
            }
            var buffer = Marshal.AllocHGlobal(checked((int)length));
            try
            {
                var written = MacAclCopyExternal(buffer, acl, length);
                if (written != length)
                {
                    throw written < 0
                        ? NativeIOException("acl_copy_ext inspect workspace target access ACL", Marshal.GetLastPInvokeError())
                        : new IOException("The workspace target access ACL changed during retained-handle capture.");
                }
                var bytes = new byte[checked((int)length)];
                Marshal.Copy(buffer, bytes, 0, bytes.Length);
                return bytes;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            _ = MacAclFree(acl);
        }
    }

    public static void RenameRelative(
        SafeFileHandle source,
        SafeFileHandle sourceParent,
        string sourceName,
        SafeFileHandle targetParent,
        string targetName,
        bool overwrite)
    {
        EnsureSimpleName(sourceName);
        EnsureSimpleName(targetName);
        RequireRegularFile(source, "workspace action rename source");
        if (OperatingSystem.IsWindows())
        {
            RenameWindowsRelative(source, targetParent, targetName, overwrite);
            return;
        }

        var sourceDescriptor = sourceParent.DangerousGetHandle().ToInt32();
        var targetDescriptor = targetParent.DangerousGetHandle().ToInt32();
        var result = overwrite
            ? UnixRenameAt(sourceDescriptor, sourceName, targetDescriptor, targetName)
            : OperatingSystem.IsLinux()
                ? UnixRenameAt2(sourceDescriptor, sourceName, targetDescriptor, targetName, UnixRenameNoReplace)
                : MacRenameAtExclusive(sourceDescriptor, sourceName, targetDescriptor, targetName, MacRenameExclusive);
        if (result != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (!overwrite && error is UnixFunctionNotImplemented or UnixInvalidArgument)
            {
                throw new PlatformNotSupportedException("Atomic no-replace publication is unavailable on this filesystem host.");
            }
            throw NativeIOException("handle-relative workspace action rename", error);
        }
    }

    public static void ExchangeRelative(
        SafeFileHandle source,
        SafeFileHandle sourceParent,
        string sourceName,
        SafeFileHandle target,
        SafeFileHandle targetParent,
        string targetName)
    {
        EnsureSimpleName(sourceName);
        EnsureSimpleName(targetName);
        RequireRegularFile(source, "workspace action exchange source");
        RequireRegularFile(target, "workspace action exchange target");
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows existing-file commits use ReplaceFileW with an authenticated private displaced-target backup.");
        }
        var result = OperatingSystem.IsLinux()
            ? UnixRenameAt2(
                sourceParent.DangerousGetHandle().ToInt32(),
                sourceName,
                targetParent.DangerousGetHandle().ToInt32(),
                targetName,
                UnixRenameExchange)
            : MacRenameAtExclusive(
                sourceParent.DangerousGetHandle().ToInt32(),
                sourceName,
                targetParent.DangerousGetHandle().ToInt32(),
                targetName,
                MacRenameSwap);
        if (result != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is UnixFunctionNotImplemented or UnixInvalidArgument)
            {
                throw new PlatformNotSupportedException("Atomic exchange publication is unavailable on this filesystem host.");
            }
            throw NativeIOException("handle-relative workspace action exchange", error);
        }
    }

    public static string CaptureWindowsReplacementPath(SafeFileHandle replacement, string replacementName)
    {
        EnsureSimpleName(replacementName);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Atomic Windows replacement is available only on Windows.");
        }
        RequireRegularFile(replacement, "workspace action replacement");
        RequireExactOpenedName(replacement, replacementName);
        return ReadFinalPath(replacement);
    }

    public static void ReplaceWindowsRelativeWithBackup(
        string replacementPath,
        SafeFileHandle replaced,
        SafeFileHandle replacedParent,
        string replacedName,
        SafeFileHandle backupParent,
        string backupName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replacementPath);
        EnsureSimpleName(replacedName);
        EnsureSimpleName(backupName);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Atomic Windows replacement is available only on Windows.");
        }
        RequireRegularFile(replaced, "workspace action replaced target");
        RequireExactOpenedName(replaced, replacedName);
        RequireDirectory(replacedParent, "workspace action replacement parent");
        RequireDirectory(backupParent, "workspace action replacement backup parent");
        using (var existingBackup = OpenRelativeFile(backupParent, backupName, allowMissing: true, write: false))
        {
            if (existingBackup is not null)
            {
                throw new IOException("The authenticated workspace replacement backup slot is already occupied.");
            }
        }
        var replacedPath = Path.Combine(ReadFinalPath(replacedParent), replacedName);
        var backupPath = Path.Combine(ReadFinalPath(backupParent), backupName);
        if (!ReplaceFile(replacedPath, replacementPath, backupPath, 0, IntPtr.Zero, IntPtr.Zero))
        {
            // https://github.com/Jacob-J-Thomas/agenthome-poc/issues/506 owns partial-error status coverage.
            throw NativeIOException("ReplaceFileW workspace action replacement", Marshal.GetLastPInvokeError());
        }
        GC.KeepAlive(replaced);
        GC.KeepAlive(replacedParent);
        GC.KeepAlive(backupParent);
    }

    public static void DeleteExact(SafeFileHandle parent, string name, SafeFileHandle retained, WorkspaceActionNativeFileStamp expected)
        => DeleteExact(parent, name, retained, expected, expectedLinkCount: 1);

    private static void DeleteExact(
        SafeFileHandle parent,
        string name,
        SafeFileHandle retained,
        WorkspaceActionNativeFileStamp expected,
        uint expectedLinkCount)
    {
        var current = GetIdentity(retained);
        if (!current.SameEntry(expected) || !current.IsRegularFile || current.LinkCount != expectedLinkCount)
        {
            throw new IOException("Authenticated workspace action staging was substituted before cleanup.");
        }
        if (OperatingSystem.IsWindows())
        {
            EnsureSimpleName(name);
            RequireExactOpenedName(retained, name);
            var disposition = Marshal.AllocHGlobal(1);
            try
            {
                Marshal.WriteByte(disposition, 1);
                if (!SetFileInformationByHandle(retained, FileDispositionInfo, disposition, 1))
                {
                    throw NativeIOException("SetFileInformationByHandle workspace stage cleanup", Marshal.GetLastPInvokeError());
                }
            }
            finally
            {
                Marshal.FreeHGlobal(disposition);
            }
            return;
        }

        using var named = OpenRelativeFile(parent, name, allowMissing: true, write: true);
        if (named is null || !GetIdentity(named).SameEntry(expected))
        {
            throw new IOException("Authenticated workspace action staging name was substituted before cleanup.");
        }
        if (UnixUnlinkAt(parent.DangerousGetHandle().ToInt32(), name, 0) != 0)
        {
            throw NativeIOException("unlinkat workspace action stage", Marshal.GetLastPInvokeError());
        }
    }

    public static void FlushFile(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!FlushFileBuffers(handle))
            {
                throw NativeIOException("FlushFileBuffers workspace action file", Marshal.GetLastPInvokeError());
            }
            return;
        }
        var descriptor = handle.DangerousGetHandle().ToInt32();
        if (UnixFsync(descriptor) != 0 || OperatingSystem.IsMacOS() && UnixFcntl(descriptor, MacFullFsync) != 0)
        {
            throw NativeIOException("workspace action file durability barrier", Marshal.GetLastPInvokeError());
        }
    }

    public static void FlushDirectory(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        if (UnixFsync(handle.DangerousGetHandle().ToInt32()) != 0)
        {
            throw NativeIOException("workspace action directory durability barrier", Marshal.GetLastPInvokeError());
        }
    }

    private static SafeFileHandle? NtCreateRelative(
        SafeFileHandle parent,
        string name,
        uint access,
        uint disposition,
        uint options,
        bool allowMissing,
        uint shareAccess)
    {
        var nameBuffer = Marshal.StringToHGlobalUni(name);
        var unicodeBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        try
        {
            var nameBytes = checked(name.Length * sizeof(char));
            var unicode = new UnicodeString
            {
                Length = checked((ushort)nameBytes),
                MaximumLength = checked((ushort)(nameBytes + sizeof(char))),
                Buffer = nameBuffer,
            };
            Marshal.StructureToPtr(unicode, unicodeBuffer, fDeleteOld: false);
            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = unicodeBuffer,
                Attributes = WindowsObjectAttributes(parent),
            };
            var status = NtCreateFile(
                out var rawHandle,
                access,
                ref attributes,
                out _,
                IntPtr.Zero,
                FileAttributeNormal,
                shareAccess,
                disposition,
                options,
                IntPtr.Zero,
                0);
            GC.KeepAlive(parent);
            if (status >= 0)
            {
                return new SafeFileHandle(rawHandle, ownsHandle: true);
            }
            var unsignedStatus = unchecked((uint)status);
            if (allowMissing && unsignedStatus is StatusObjectNameNotFound or StatusObjectPathNotFound)
            {
                return null;
            }
            throw new IOException($"NtCreateFile workspace action open failed closed with NTSTATUS 0x{unsignedStatus:x8}.");
        }
        finally
        {
            Marshal.FreeHGlobal(unicodeBuffer);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    private static void RenameWindowsRelative(SafeFileHandle source, SafeFileHandle targetParent, string targetName, bool overwrite)
    {
        var nameBytes = Encoding.Unicode.GetBytes(targetName);
        var rootDirectoryOffset = IntPtr.Size == 8 ? 8 : 4;
        var fileNameLengthOffset = rootDirectoryOffset + IntPtr.Size;
        var fileNameOffset = fileNameLengthOffset + sizeof(uint);
        var unalignedSize = checked(fileNameOffset + nameBytes.Length + sizeof(char));
        var bufferSize = checked((unalignedSize + IntPtr.Size - 1) & -IntPtr.Size);
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            Marshal.Copy(new byte[bufferSize], 0, buffer, bufferSize);
            var flags = overwrite
                ? FileRenameReplaceIfExists | FileRenamePosixSemantics | FileRenameIgnoreReadOnlyAttribute
                : 0u;
            Marshal.WriteInt32(buffer, unchecked((int)flags));
            Marshal.WriteIntPtr(buffer, rootDirectoryOffset, targetParent.DangerousGetHandle());
            Marshal.WriteInt32(buffer, fileNameLengthOffset, nameBytes.Length);
            Marshal.Copy(nameBytes, 0, IntPtr.Add(buffer, fileNameOffset), nameBytes.Length);
            var status = NtSetInformationFile(source, out _, buffer, (uint)bufferSize, FileRenameInformationEx);
            GC.KeepAlive(targetParent);
            if (status < 0)
            {
                throw new IOException($"NtSetInformationFile workspace action rename failed closed with NTSTATUS 0x{unchecked((uint)status):x8}.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [SupportedOSPlatform("windows")]
    private static RawSecurityDescriptor ReadWindowsReplacementSecurityDescriptor(SafeFileHandle handle)
    {
        var securityDescriptor = IntPtr.Zero;
        try
        {
            var status = GetSecurityInfo(
                handle,
                SeFileObject,
                OwnerSecurityInformation | GroupSecurityInformation | DaclSecurityInformation,
                out _,
                out _,
                out _,
                out _,
                out securityDescriptor);
            if (status != 0)
            {
                throw new UnauthorizedAccessException(
                    "The retained Windows workspace replacement handle did not return owner, group, and DACL metadata.",
                    new Win32Exception(unchecked((int)status)));
            }
            if (securityDescriptor == IntPtr.Zero)
            {
                throw new UnauthorizedAccessException("The retained Windows workspace replacement handle returned an empty security descriptor.");
            }
            var length = GetSecurityDescriptorLength(securityDescriptor);
            if (length is 0 or > MaximumWindowsReplacementSecurityDescriptorBytes)
            {
                throw new UnauthorizedAccessException("The retained Windows workspace replacement handle returned an invalid security descriptor length.");
            }
            var binary = new byte[checked((int)length)];
            Marshal.Copy(securityDescriptor, binary, 0, binary.Length);
            try
            {
                return new RawSecurityDescriptor(binary, 0);
            }
            catch (ArgumentException exception)
            {
                throw new UnauthorizedAccessException("The retained Windows workspace replacement handle returned an invalid security descriptor.", exception);
            }
        }
        finally
        {
            if (securityDescriptor != IntPtr.Zero && LocalFree(securityDescriptor) != IntPtr.Zero)
            {
                throw new UnauthorizedAccessException(
                    "The retained Windows workspace replacement security descriptor could not be released.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RequireWindowsReplacementMetadata(
        SafeFileHandle source,
        SafeFileHandle replacement,
        bool? expectedProtectedDiscretionaryAccessControlList)
    {
        var sourceDescriptor = ReadWindowsReplacementSecurityDescriptor(source);
        var replacementDescriptor = ReadWindowsReplacementSecurityDescriptor(replacement);
        var expectedProtection = expectedProtectedDiscretionaryAccessControlList
            ?? sourceDescriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected);
        if (sourceDescriptor.Owner is null
            || replacementDescriptor.Owner is null
            || !sourceDescriptor.Owner.Equals(replacementDescriptor.Owner)
            || sourceDescriptor.Group is null
            || replacementDescriptor.Group is null
            || !sourceDescriptor.Group.Equals(replacementDescriptor.Group)
            || !sourceDescriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclPresent)
            || !replacementDescriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclPresent)
            || sourceDescriptor.DiscretionaryAcl is null
            || replacementDescriptor.DiscretionaryAcl is null
            || replacementDescriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected) != expectedProtection
            || !GetBinaryForm(sourceDescriptor.DiscretionaryAcl)
                .AsSpan()
                .SequenceEqual(GetBinaryForm(replacementDescriptor.DiscretionaryAcl)))
        {
            throw new IOException("The published Windows workspace replacement did not preserve exact owner, primary-group, DACL, and DACL-protection metadata.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] GetBinaryForm(GenericAcl accessControlList)
    {
        var bytes = new byte[accessControlList.BinaryLength];
        accessControlList.GetBinaryForm(bytes, 0);
        return bytes;
    }

    private static void EnsureSimpleName(string name)
    {
        if (string.IsNullOrEmpty(name)
            || name is "." or ".."
            || name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0
            || name.Contains('\0'))
        {
            throw new ArgumentException("A retained-handle filesystem operation requires one exact simple name.", nameof(name));
        }
    }

    private static SafeFileHandle RetainValidated(SafeFileHandle handle, Action<SafeFileHandle> validate)
    {
        try
        {
            validate(handle);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static int DuplicateUnixCloseOnExec(int descriptor) => SystemNativeDuplicate(descriptor);

    private static uint WindowsObjectAttributes(SafeFileHandle parent)
    {
        if (!GetFileInformationByHandleEx(
                parent,
                FileCaseSensitiveInfo,
                out WindowsCaseSensitiveInformation information,
                (uint)Marshal.SizeOf<WindowsCaseSensitiveInformation>()))
        {
            throw NativeIOException("GetFileInformationByHandleEx FileCaseSensitiveInfo", Marshal.GetLastPInvokeError());
        }
        return (information.Flags & WindowsCaseSensitiveDirectory) == 0
            ? ObjectAttributeCaseInsensitive
            : 0;
    }

    private static void RequireUnixPlatform()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("Governed workspace native actions support Windows, Linux, and macOS.");
        }
    }

    private static IOException NativeIOException(string operation, int error)
        => new($"{operation} failed closed with native error {error}.", new Win32Exception(error));

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string path, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int informationClass,
        out CapabilityCatalogFileIdInfo information,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int informationClass,
        out WindowsCaseSensitiveInformation information,
        uint bufferSize);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        StringBuilder path,
        int pathLength,
        uint flags);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationByHandle(
        SafeFileHandle file,
        StringBuilder? volumeName,
        int volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        StringBuilder fileSystemName,
        int fileSystemNameSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, IntPtr information, uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle file);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "ReplaceFileW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReplaceFile(string replacedFileName, string replacementFileName, string backupFileName, uint replaceFlags, IntPtr exclude, IntPtr reserved);


    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(
        IntPtr sourceProcess,
        SafeFileHandle sourceHandle,
        IntPtr targetProcess,
        out SafeFileHandle targetHandle,
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint options);

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(out IntPtr file, uint desiredAccess, ref ObjectAttributes objectAttributes, out IoStatusBlock ioStatusBlock, IntPtr allocationSize, uint fileAttributes, uint shareAccess, uint createDisposition, uint createOptions, IntPtr eaBuffer, uint eaLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryDirectoryFile(SafeFileHandle file, IntPtr eventHandle, IntPtr apcRoutine, IntPtr apcContext, out IoStatusBlock ioStatusBlock, IntPtr fileInformation, uint length, int fileInformationClass, byte returnSingleEntry, IntPtr fileName, byte restartScan);

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(SafeFileHandle file, out IoStatusBlock ioStatusBlock, IntPtr fileInformation, uint length, int fileInformationClass);

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
    private static extern uint SetSecurityInfo(
        SafeFileHandle handle,
        int objectType,
        uint securityInformation,
        IntPtr owner,
        IntPtr group,
        IntPtr discretionaryAccessControlList,
        IntPtr systemAccessControlList);

    [DllImport("advapi32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    private static extern uint GetSecurityDescriptorLength(IntPtr securityDescriptor);

    [DllImport("kernel32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int UnixOpen(string path, int flags);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int UnixOpenAt(int directory, string path, int flags, int mode);

    [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)]
    private static extern int UnixMkdirAt(int directory, string path, int mode);

    [DllImport("libc", EntryPoint = "renameat", SetLastError = true)]
    private static extern int UnixRenameAt(int oldDirectory, string oldPath, int newDirectory, string newPath);

    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int UnixRenameAt2(int oldDirectory, string oldPath, int newDirectory, string newPath, uint flags);

    [DllImport("libc", EntryPoint = "renameatx_np", SetLastError = true)]
    private static extern int MacRenameAtExclusive(int oldDirectory, string oldPath, int newDirectory, string newPath, uint flags);

    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
    private static extern int UnixUnlinkAt(int directory, string path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int UnixFsync(int descriptor);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int UnixFcntl(int descriptor, int command);

    [DllImport("libc", EntryPoint = "readlink", SetLastError = true)]
    private static extern nint UnixReadLink(string path, [Out] byte[] buffer, nuint bufferSize);

    [DllImport("/usr/lib/libproc.dylib", EntryPoint = "proc_pidfdinfo", SetLastError = true)]
    private static extern int MacProcPidFdInfo(int processId, int descriptor, int flavor, IntPtr buffer, int bufferSize);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int MacFstat(int descriptor, out CapabilityCatalogMacStat buffer);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int LinuxStatx(int directory, string path, int flags, uint mask, out LinuxStatxBuffer buffer);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int LinuxIoctlGetVersion64(int descriptor, nuint request, out long generation);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int LinuxIoctlGetVersion32(int descriptor, nuint request, out int generation);

    [DllImport("libc", EntryPoint = "fchmod", SetLastError = true)]
    private static extern int UnixFchmod(int descriptor, int mode);

    [DllImport("libc", EntryPoint = "fchown", SetLastError = true)]
    private static extern int UnixFchown(int descriptor, uint ownerId, uint groupId);

    [DllImport("libc", EntryPoint = "fgetxattr", SetLastError = true)]
    private static extern nint LinuxFGetXattr(int descriptor, string name, IntPtr value, nuint size);

    [DllImport("libc", EntryPoint = "fsetxattr", SetLastError = true)]
    private static extern int LinuxFSetXattr(int descriptor, string name, IntPtr value, nuint size, int flags);

    [DllImport("libc", EntryPoint = "fremovexattr", SetLastError = true)]
    private static extern int LinuxFRemoveXattr(int descriptor, string name);

    [DllImport("libc", EntryPoint = "fcopyfile", SetLastError = true)]
    private static extern int MacCopyFile(int sourceDescriptor, int targetDescriptor, IntPtr state, uint flags);

    [DllImport("libc", EntryPoint = "acl_get_fd", SetLastError = true)]
    private static extern IntPtr MacAclGetFd(int descriptor);

    [DllImport("libc", EntryPoint = "acl_init", SetLastError = true)]
    private static extern IntPtr MacAclInit(int count);

    [DllImport("libc", EntryPoint = "acl_set_fd_np", SetLastError = true)]
    private static extern int MacAclSetFd(int descriptor, IntPtr acl, int aclType);

    [DllImport("libc", EntryPoint = "acl_size", SetLastError = true)]
    private static extern nint MacAclSize(IntPtr acl);

    [DllImport("libc", EntryPoint = "acl_copy_ext", SetLastError = true)]
    private static extern nint MacAclCopyExternal(IntPtr buffer, IntPtr acl, nint size);

    [DllImport("libc", EntryPoint = "acl_free", SetLastError = true)]
    private static extern int MacAclFree(IntPtr acl);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int UnixClose(int descriptor);

    [DllImport("System.Native", EntryPoint = "SystemNative_Dup", SetLastError = true)]
    private static extern int SystemNativeDuplicate(int descriptor);

    [DllImport("libc", EntryPoint = "fdopendir", SetLastError = true)]
    private static extern IntPtr UnixFdOpenDirectory(int descriptor);

    [DllImport("libc", EntryPoint = "readdir", SetLastError = true)]
    private static extern IntPtr UnixReadDirectory(IntPtr directory);

    [DllImport("libc", EntryPoint = "rewinddir")]
    private static extern void UnixRewindDirectory(IntPtr directory);

    [DllImport("libc", EntryPoint = "closedir", SetLastError = true)]
    private static extern int UnixCloseDirectory(IntPtr directory);

    private static int UnixDirectory => OperatingSystem.IsMacOS() ? 0x00100000 : 0x00010000;
    private static int UnixNoFollow => OperatingSystem.IsMacOS() ? 0x00000100 : 0x00020000;
    private static int UnixCloseOnExec => OperatingSystem.IsMacOS() ? 0x01000000 : 0x00080000;
    private static int UnixCreate => OperatingSystem.IsMacOS() ? 0x00000200 : 0x00000040;
    private static int UnixExclusive => OperatingSystem.IsMacOS() ? 0x00000800 : 0x00000080;
    private static int UnixNonBlocking => OperatingSystem.IsMacOS() ? 0x00000004 : 0x00000800;
    private static int UnixSymbolicLinkLoop => OperatingSystem.IsMacOS() ? 62 : 40;
    private const int UnixReadOnly = 0;
    private const int UnixReadWrite = 2;
    private const int UnixNoEntry = 2;
    private const int UnixAlreadyExists = 17;
    private const int UnixInvalidArgument = 22;
    private const int UnixNotDirectory = 20;
    private const int UnixFunctionNotImplemented = 38;
    private const int LinuxNoData = 61;
    private const int MaximumAccessControlBytes = 64 * 1024;
    private const string LinuxAccessAclName = "system.posix_acl_access";
    private const string LinuxDefaultAclName = "system.posix_acl_default";
    private const uint UnixRenameNoReplace = 1;
    private const uint UnixRenameExchange = 2;
    private const uint MacRenameExclusive = 4;
    private const uint MacRenameSwap = 2;
    private const int PermissionUserReadWrite = 0x180;
    private const int PermissionUserReadWriteExecute = 0x1C0;
    private const int MacFullFsync = 51;
    private const int MacProcessFdVnodePathInfo = 2;
    private const int MacVnodeFdInfoWithPathBytes = 1_200;
    private const int MacVnodePathOffset = 176;
    private const int MacMaximumPathBytes = 1_024;
    private const int MaximumNativePathBytes = 32_768;
    private const uint MacCopyFileSecurity = 3;
    private const int MacExtendedAclType = 0x00000100;
    private const int LinuxAtNoAutomount = 0x800;
    private const int LinuxAtEmptyPath = 0x1000;
    private const uint LinuxStatxBasicStats = 0x07ff;
    private const uint LinuxStatxBirthTime = 0x00000800;
    private const uint LinuxStatxMountIdUnique = 0x00004000;
    private const nuint LinuxGetVersion64 = 0x80087601;
    private const nuint LinuxGetVersion32 = 0x80047601;
    private const uint UnixFileTypeMask = 0xF000;
    private const uint UnixPermissionMask = 0x0FFF;
    private const uint UnixDirectoryType = 0x4000;
    private const uint UnixRegularFileType = 0x8000;
    private const uint UnixSymbolicLinkType = 0xA000;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint ReadControl = 0x00020000;
    private const uint WriteDac = 0x00040000;
    private const uint WriteOwner = 0x00080000;
    private const uint PrivateSecurityAccess = ReadControl | WriteDac | WriteOwner;
    private const uint SynchronizeAccess = 0x00100000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagWriteThrough = 0x80000000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint DuplicateSameAccess = 0x00000002;
    private const uint ObjectAttributeCaseInsensitive = 0x00000040;
    private const uint NtOpen = 1;
    private const uint NtCreate = 2;
    private const uint NtOpenIf = 3;
    private const uint NtDirectoryFile = 0x00000001;
    private const uint NtWriteThrough = 0x00000002;
    private const uint NtSynchronousIoNonAlert = 0x00000020;
    private const uint NtNonDirectoryFile = 0x00000040;
    private const uint NtOpenReparsePoint = 0x00200000;
    private const uint StatusObjectNameNotFound = 0xC0000034;
    private const uint StatusObjectPathNotFound = 0xC000003A;
    private const uint StatusNoMoreFiles = 0x80000006;
    private const int FileDirectoryInformation = 1;
    private const int WindowsFileDirectoryInformationNameLengthOffset = 60;
    private const int WindowsFileDirectoryInformationNameOffset = 64;
    private const uint WindowsMaximumFileNameBytes = 510;
    private const int UnixDirectoryRecordLengthOffset = 16;
    private const int LinuxDirectoryNameOffset = 19;
    private const int MacDirectoryNameOffset = 21;
    private const int UnixMaximumFileNameBytes = 255;
    private const int UnixMaximumDirectoryRecordBytes = 1_280;
    private const int FileRenameInformationEx = 65;
    private const int FileIdInfo = 18;
    private const int FileCaseSensitiveInfo = 23;
    private const uint WindowsCaseSensitiveDirectory = 0x00000001;
    private struct WindowsCaseSensitiveInformation
    {
        public uint Flags;
    }
    private const int FileDispositionInfo = 4;
    private const uint FileRenameReplaceIfExists = 0x00000001;
    private const uint FileRenamePosixSemantics = 0x00000002;
    private const uint FileRenameIgnoreReadOnlyAttribute = 0x00000040;
    private const int SeFileObject = 1;
    private const uint OwnerSecurityInformation = 0x00000001;
    private const uint GroupSecurityInformation = 0x00000002;
    private const uint DaclSecurityInformation = 0x00000004;
    private const uint UnprotectedDaclSecurityInformation = 0x20000000;
    private const uint ProtectedDaclSecurityInformation = 0x80000000;
    private const uint MaximumWindowsReplacementSecurityDescriptorBytes = 128 * 1024;
}
