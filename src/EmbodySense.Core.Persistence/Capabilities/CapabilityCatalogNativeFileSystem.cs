using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EmbodySense.Core.Persistence.Capabilities;

/// <summary>Provides the minimal no-follow native filesystem surface required by catalog persistence.</summary>
internal static class CapabilityCatalogNativeFileSystem
{
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeReparsePoint = 0x400;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagWriteThrough = 0x80000000;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint FileShareDelete = 0x4;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint CreateNew = 1;
    private const uint OpenExisting = 3;
    private const uint OpenAlways = 4;
    private const uint MoveFileReplaceExisting = 0x1;
    private const uint MoveFileWriteThrough = 0x8;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int AtEmptyPath = 0x1000;
    private const int LockExclusive = 2;
    private const int LockNonBlocking = 4;
    private const int PermissionUserReadWrite = 0x180;
    private const ushort UnixFileTypeMask = 0xF000;
    private const ushort UnixRegularFile = 0x8000;
    private const uint StatxMode = 0x2;
    private const uint StatxInode = 0x100;

    public static SafeFileHandle? OpenDirectory(string fullPath, SafeFileHandle? parent, string? name, bool create, ICapabilityCatalogDurabilityBarrier durabilityBarrier, out bool created)
    {
        return OperatingSystem.IsWindows() ? OpenWindowsDirectory(fullPath, create, durabilityBarrier, out created) : OpenUnixDirectory(parent, name, create, out created);
    }

    public static SafeFileHandle? OpenRegularFile(string fullPath, SafeFileHandle parent, string name, FileMode mode, FileAccess access, FileShare share, bool writeThrough)
    {
        return OperatingSystem.IsWindows() ? OpenWindowsFile(fullPath, mode, access, share, writeThrough) : OpenUnixFile(parent, name, mode, access);
    }

    public static FileStream? TryAcquireExclusiveLock(string fullPath, SafeFileHandle parent, string name)
    {
        if (OperatingSystem.IsWindows())
        {
            var handle = OpenWindowsHandle(fullPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, writeThrough: true, allowDirectory: false, returnNullWhenMissing: false, returnNullWhenContended: true);
            return handle is null ? null : new FileStream(handle, FileAccess.ReadWrite, 1, isAsync: false);
        }

        var unixHandle = OpenUnixFile(parent, name, FileMode.OpenOrCreate, FileAccess.ReadWrite) ?? throw new IOException("The capability catalog lock parent is unavailable.");
        if (flock(unixHandle, LockExclusive | LockNonBlocking) == 0)
        {
            return new FileStream(unixHandle, FileAccess.ReadWrite, 1, isAsync: false);
        }

        var error = Marshal.GetLastPInvokeError();
        unixHandle.Dispose();
        if (error is 11 or 35)
        {
            return null;
        }

        throw NativeIOException("The capability catalog lock could not be acquired", error);
    }

    public static void MoveFile(string sourceFullPath, string destinationFullPath, SafeFileHandle parent, string sourceName, string destinationName)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!MoveFileEx(sourceFullPath, destinationFullPath, MoveFileReplaceExisting | MoveFileWriteThrough))
            {
                throw NativeIOException("The capability catalog artifact could not be moved atomically and durably", Marshal.GetLastPInvokeError());
            }
            return;
        }

        if (renameat(parent, sourceName, parent, destinationName) != 0)
        {
            throw NativeIOException("The capability catalog artifact could not be moved atomically", Marshal.GetLastPInvokeError());
        }
    }

    public static void DeleteFileIfPresent(string fullPath, SafeFileHandle parent, string name)
    {
        if (OperatingSystem.IsWindows())
        {
            File.Delete(fullPath);
            return;
        }

        if (unlinkat(parent, name, 0) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != 2)
            {
                throw NativeIOException("The capability catalog temporary artifact could not be removed", error);
            }
        }
    }

    public static void FlushToDisk(FileStream stream)
    {
        if (OperatingSystem.IsWindows())
        {
            stream.Flush(flushToDisk: true);
        }
        else if (fsync(stream.SafeFileHandle) != 0)
        {
            throw NativeIOException("The capability catalog artifact could not be flushed durably", Marshal.GetLastPInvokeError());
        }
    }

    public static void FlushAfterRename(string destinationFullPath, SafeFileHandle parent)
    {
        if (OperatingSystem.IsWindows())
        {
            using var destination = OpenWindowsHandle(destinationFullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, writeThrough: true, allowDirectory: false, returnNullWhenMissing: false, returnNullWhenContended: false) ?? throw new IOException("The renamed capability catalog artifact is unavailable for its durability barrier.");
            if (!FlushFileBuffers(destination))
            {
                throw NativeIOException("The renamed capability catalog artifact could not be flushed durably", Marshal.GetLastPInvokeError());
            }
            return;
        }

        if (fsync(parent) != 0)
        {
            throw NativeIOException("The capability catalog parent-directory rename metadata could not be flushed durably", Marshal.GetLastPInvokeError());
        }
    }

    public static void FlushAfterDirectoryCreate(SafeFileHandle parent)
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows directory creation already used a retained-handle rename followed by FlushFileBuffers.
            return;
        }

        if (fsync(parent) != 0)
        {
            throw NativeIOException("The capability catalog parent-directory creation metadata could not be flushed durably", Marshal.GetLastPInvokeError());
        }
    }

    public static void SetUserOnlyPermissions(SafeFileHandle file)
    {
        if (!OperatingSystem.IsWindows() && fchmod(file, PermissionUserReadWrite) != 0)
        {
            throw NativeIOException("The capability catalog authentication key permissions could not be restricted", Marshal.GetLastPInvokeError());
        }
    }

    public static string GetPhysicalIdentityMaterial(SafeFileHandle directory)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!GetFileInformationByHandleEx(directory, FileInfoByHandleClass.FileIdInfo, out CapabilityCatalogFileIdInfo information, (uint)Marshal.SizeOf<CapabilityCatalogFileIdInfo>()))
            {
                throw NativeIOException("The capability catalog workspace physical identity could not be read", Marshal.GetLastPInvokeError());
            }

            return $"windows:{information.VolumeSerialNumber:x16}:{Convert.ToHexString(information.FileId.ToByteArray()).ToLowerInvariant()}";
        }

        // TODO(#271): Review the Linux/macOS device/inode physical identity lifetime.
        if (OperatingSystem.IsLinux())
        {
            if (statx(directory, string.Empty, AtEmptyPath, StatxInode, out var information) != 0 || (information.Mask & StatxInode) == 0)
            {
                throw NativeIOException("The capability catalog workspace physical identity could not be read", Marshal.GetLastPInvokeError());
            }

            return $"linux:{information.DeviceMajor:x8}:{information.DeviceMinor:x8}:{information.Inode:x16}";
        }

        if (OperatingSystem.IsMacOS())
        {
            if (fstat(directory, out CapabilityCatalogMacStat information) != 0)
            {
                throw NativeIOException("The capability catalog workspace physical identity could not be read", Marshal.GetLastPInvokeError());
            }

            return $"macos:{information.Device:x8}:{information.Inode:x16}";
        }

        throw new PlatformNotSupportedException("Capability catalog physical workspace identity supports Windows, Linux, and macOS.");
    }

    public static string GetDirectoryEnumerationPath(SafeFileHandle directory)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows directory enumeration uses the leased canonical path.");
        }

        var descriptor = directory.DangerousGetHandle().ToInt64();
        var procPath = $"/proc/self/fd/{descriptor}";
        if (Directory.Exists(procPath))
        {
            return procPath;
        }

        var devicePath = $"/dev/fd/{descriptor}";
        return Directory.Exists(devicePath) ? devicePath : throw new IOException("No handle-relative directory enumeration surface is available on this platform.");
    }

    private static SafeFileHandle? OpenWindowsDirectory(string fullPath, bool create, ICapabilityCatalogDurabilityBarrier durabilityBarrier, out bool created)
    {
        created = false;
        var handle = CreateFile(fullPath, 0, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            if (!create && (error == ErrorFileNotFound || error == ErrorPathNotFound))
            {
                return null;
            }
            if (create && (error == ErrorFileNotFound || error == ErrorPathNotFound))
            {
                handle = CreateWindowsDirectoryDurably(fullPath, durabilityBarrier);
                created = true;
            }
            else
            {
                throw NativeIOException($"Capability catalog directory `{fullPath}` could not be opened safely", error);
            }
        }

        ValidateWindowsHandle(handle, fullPath, requireDirectory: true);
        return handle;
    }

    private static SafeFileHandle CreateWindowsDirectoryDurably(string fullPath, ICapabilityCatalogDurabilityBarrier durabilityBarrier)
    {
        var parentPath = Path.GetDirectoryName(fullPath) ?? throw new IOException("Capability catalog directory has no parent.");
        var temporaryPath = Path.Combine(parentPath, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.mkdir");
        if (!CreateDirectory(temporaryPath, IntPtr.Zero))
        {
            throw NativeIOException($"Capability catalog staging directory `{temporaryPath}` could not be created", Marshal.GetLastPInvokeError());
        }

        SafeFileHandle? staging = null;
        SafeFileHandle? movedIdentity = null;
        var renamed = false;
        try
        {
            staging = CreateFile(temporaryPath, GenericRead | GenericWrite | DeleteAccess, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint | FileFlagWriteThrough, IntPtr.Zero);
            if (staging.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                staging.Dispose();
                staging = null;
                throw NativeIOException($"Capability catalog staging directory `{temporaryPath}` could not be retained safely", error);
            }

            ValidateWindowsHandle(staging, temporaryPath, requireDirectory: true);
            var expectedIdentity = GetWindowsFileIdentity(staging, temporaryPath);
            durabilityBarrier.BeforeDirectoryMove(temporaryPath, fullPath);
            RenameWindowsDirectoryByHandle(staging, fullPath);
            renamed = true;
            if (!FlushFileBuffers(staging))
            {
                throw NativeIOException($"Capability catalog directory `{fullPath}` could not be flushed after its handle-based move", Marshal.GetLastPInvokeError());
            }

            movedIdentity = CreateFile(fullPath, 0, FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
            if (movedIdentity.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                movedIdentity.Dispose();
                movedIdentity = null;
                throw NativeIOException($"New capability catalog directory `{fullPath}` could not be identity-checked safely", error);
            }
            ValidateWindowsHandle(movedIdentity, fullPath, requireDirectory: true);
            RequireSameWindowsFileIdentity(expectedIdentity, GetWindowsFileIdentity(movedIdentity, fullPath), fullPath);

            staging.Dispose();
            staging = null;
            durabilityBarrier.AfterDirectoryMove(temporaryPath, fullPath);

            var retained = CreateFile(fullPath, 0, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
            if (retained.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                retained.Dispose();
                throw NativeIOException($"New capability catalog directory `{fullPath}` could not be retained without delete sharing", error);
            }

            try
            {
                ValidateWindowsHandle(retained, fullPath, requireDirectory: true);
                RequireSameWindowsFileIdentity(expectedIdentity, GetWindowsFileIdentity(retained, fullPath), fullPath);
                return retained;
            }
            catch
            {
                retained.Dispose();
                throw;
            }
        }
        finally
        {
            if (!renamed && staging is not null && !staging.IsInvalid && !staging.IsClosed)
            {
                MarkWindowsDirectoryForDeletion(staging);
            }
            staging?.Dispose();
            movedIdentity?.Dispose();
        }
    }

    private static void RenameWindowsDirectoryByHandle(SafeFileHandle directory, string destinationPath)
    {
        var fileName = System.Text.Encoding.Unicode.GetBytes(destinationPath);
        var rootDirectoryOffset = IntPtr.Size == 8 ? 8 : 4;
        var fileNameLengthOffset = rootDirectoryOffset + IntPtr.Size;
        var fileNameOffset = fileNameLengthOffset + sizeof(uint);
        var information = new byte[fileNameOffset + fileName.Length];
        BitConverter.GetBytes(fileName.Length).CopyTo(information, fileNameLengthOffset);
        fileName.CopyTo(information, fileNameOffset);
        if (!SetFileInformationByHandle(directory, FileInfoByHandleClass.FileRenameInfo, information, (uint)information.Length))
        {
            throw NativeIOException($"Capability catalog staging directory could not be moved by retained handle to `{destinationPath}`", Marshal.GetLastPInvokeError());
        }
    }

    private static void MarkWindowsDirectoryForDeletion(SafeFileHandle directory)
    {
        if (!SetFileInformationByHandle(directory, FileInfoByHandleClass.FileDispositionInfo, [1], 1))
        {
            throw NativeIOException("Capability catalog staging directory could not be marked for exact cleanup", Marshal.GetLastPInvokeError());
        }
    }

    private static CapabilityCatalogFileIdInfo GetWindowsFileIdentity(SafeFileHandle directory, string path)
    {
        if (!GetFileInformationByHandleEx(directory, FileInfoByHandleClass.FileIdInfo, out CapabilityCatalogFileIdInfo information, (uint)Marshal.SizeOf<CapabilityCatalogFileIdInfo>()))
        {
            throw NativeIOException($"Capability catalog directory identity for `{path}` could not be read", Marshal.GetLastPInvokeError());
        }
        return information;
    }

    private static void RequireSameWindowsFileIdentity(CapabilityCatalogFileIdInfo expected, CapabilityCatalogFileIdInfo actual, string path)
    {
        if (expected.VolumeSerialNumber != actual.VolumeSerialNumber || expected.FileId != actual.FileId)
        {
            throw new IOException($"Capability catalog directory `{path}` was substituted during durable creation.");
        }
    }

    private static SafeFileHandle? OpenWindowsFile(string fullPath, FileMode mode, FileAccess access, FileShare share, bool writeThrough)
    {
        return OpenWindowsHandle(fullPath, mode, access, share, writeThrough, allowDirectory: false, returnNullWhenMissing: mode == FileMode.Open, returnNullWhenContended: false);
    }

    private static SafeFileHandle? OpenWindowsHandle(string fullPath, FileMode mode, FileAccess access, FileShare share, bool writeThrough, bool allowDirectory, bool returnNullWhenMissing, bool returnNullWhenContended)
    {
        var desiredAccess = access switch
        {
            FileAccess.Read => GenericRead,
            FileAccess.Write => GenericWrite,
            FileAccess.ReadWrite => GenericRead | GenericWrite,
            _ => throw new ArgumentOutOfRangeException(nameof(access))
        };
        var shareMode = 0U;
        if (share.HasFlag(FileShare.Read))
        {
            shareMode |= FileShareRead;
        }
        if (share.HasFlag(FileShare.Write))
        {
            shareMode |= FileShareWrite;
        }
        if (share.HasFlag(FileShare.Delete))
        {
            shareMode |= FileShareDelete;
        }

        var disposition = mode switch
        {
            FileMode.CreateNew => CreateNew,
            FileMode.Open => OpenExisting,
            FileMode.OpenOrCreate => OpenAlways,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), "Capability catalog native opens support only create-new, open, and open-or-create modes.")
        };
        var flags = FileFlagOpenReparsePoint | (writeThrough ? FileFlagWriteThrough : 0);
        var handle = CreateFile(fullPath, desiredAccess, shareMode, IntPtr.Zero, disposition, flags, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            if ((returnNullWhenMissing && error is ErrorFileNotFound or ErrorPathNotFound) || (returnNullWhenContended && error is ErrorSharingViolation or ErrorLockViolation))
            {
                return null;
            }

            throw NativeIOException($"Capability catalog file `{fullPath}` could not be opened safely", error);
        }

        ValidateWindowsHandle(handle, fullPath, requireDirectory: allowDirectory);
        return handle;
    }

    private static SafeFileHandle? OpenUnixDirectory(SafeFileHandle? parent, string? name, bool create, out bool created)
    {
        created = false;
        var flags = UnixOpenReadOnly | UnixOpenDirectory | UnixOpenNoFollow | UnixOpenCloseOnExec;
        if (parent is null)
        {
            var root = open("/", flags, 0);
            return root >= 0 ? new SafeFileHandle(new IntPtr(root), ownsHandle: true) : throw NativeIOException("The filesystem root could not be opened safely", Marshal.GetLastPInvokeError());
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var descriptor = openat(parent, name, flags, 0);
        if (descriptor < 0 && create && Marshal.GetLastPInvokeError() == 2)
        {
            if (mkdirat(parent, name, 0x1C0) == 0)
            {
                created = true;
            }
            else if (Marshal.GetLastPInvokeError() != 17)
            {
                throw NativeIOException($"Capability catalog directory `{name}` could not be created safely", Marshal.GetLastPInvokeError());
            }
            descriptor = openat(parent, name, flags, 0);
        }

        if (descriptor >= 0)
        {
            return new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
        }

        var error = Marshal.GetLastPInvokeError();
        if (!create && error == 2)
        {
            return null;
        }

        throw NativeIOException($"Capability catalog directory `{name}` could not be opened without following links", error);
    }

    private static SafeFileHandle? OpenUnixFile(SafeFileHandle parent, string name, FileMode mode, FileAccess access)
    {
        var flags = access switch
        {
            FileAccess.Read => UnixOpenReadOnly,
            FileAccess.Write => UnixOpenWriteOnly,
            FileAccess.ReadWrite => UnixOpenReadWrite,
            _ => throw new ArgumentOutOfRangeException(nameof(access))
        };
        flags |= UnixOpenNoFollow | UnixOpenCloseOnExec | UnixOpenNonBlocking;
        flags |= mode switch
        {
            FileMode.CreateNew => UnixOpenCreate | UnixOpenExclusive,
            FileMode.Open => 0,
            FileMode.OpenOrCreate => UnixOpenCreate,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        var descriptor = openat(parent, name, flags, PermissionUserReadWrite);
        if (descriptor >= 0)
        {
            var handle = new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
            try
            {
                RequireUnixRegularFile(handle, name);
                return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        var error = Marshal.GetLastPInvokeError();
        if (mode == FileMode.Open && error == 2)
        {
            return null;
        }

        throw NativeIOException($"Capability catalog file `{name}` could not be opened without following links", error);
    }

    private static void RequireUnixRegularFile(SafeFileHandle handle, string name)
    {
        ushort mode;
        if (OperatingSystem.IsLinux())
        {
            if (statx(handle, string.Empty, AtEmptyPath, StatxMode, out var information) != 0)
            {
                throw NativeIOException($"Capability catalog file metadata for `{name}` could not be read", Marshal.GetLastPInvokeError());
            }
            if ((information.Mask & StatxMode) == 0)
            {
                throw new IOException($"Capability catalog file metadata for `{name}` omitted its filesystem entry type.");
            }
            mode = information.Mode;
        }
        else if (OperatingSystem.IsMacOS())
        {
            if (fstat(handle, out CapabilityCatalogMacStat information) != 0)
            {
                throw NativeIOException($"Capability catalog file metadata for `{name}` could not be read", Marshal.GetLastPInvokeError());
            }
            mode = information.Mode;
        }
        else
        {
            throw new PlatformNotSupportedException("Capability catalog regular-file validation supports Windows, Linux, and macOS.");
        }

        if ((mode & UnixFileTypeMask) != UnixRegularFile)
        {
            throw new IOException($"Capability catalog file `{name}` is not a regular file.");
        }
    }

    private static void ValidateWindowsHandle(SafeFileHandle handle, string path, bool requireDirectory)
    {
        if (!GetFileInformationByHandleEx(handle, FileInfoByHandleClass.FileAttributeTagInfo, out CapabilityCatalogFileAttributeTagInfo information, (uint)Marshal.SizeOf<CapabilityCatalogFileAttributeTagInfo>()))
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw NativeIOException($"Capability catalog filesystem metadata for `{path}` could not be read", error);
        }

        var isDirectory = (information.FileAttributes & FileAttributeDirectory) != 0;
        if ((information.FileAttributes & FileAttributeReparsePoint) != 0 || requireDirectory != isDirectory)
        {
            handle.Dispose();
            throw new IOException($"Capability catalog persistence refuses reparse points or mismatched filesystem entry types: `{path}`.");
        }
    }

    private static IOException NativeIOException(string message, int error)
    {
        return new IOException($"{message}: {new Win32Exception(error).Message}", unchecked((int)(0x80070000U | (uint)error)));
    }

    private static int UnixOpenReadOnly => 0;
    private static int UnixOpenWriteOnly => 1;
    private static int UnixOpenReadWrite => 2;
    private static int UnixOpenCreate => OperatingSystem.IsMacOS() ? 0x200 : 0x40;
    private static int UnixOpenExclusive => OperatingSystem.IsMacOS() ? 0x800 : 0x80;
    private static int UnixOpenNoFollow => OperatingSystem.IsMacOS() ? 0x100 : 0x20000;
    private static int UnixOpenDirectory => OperatingSystem.IsMacOS() ? 0x100000 : 0x10000;
    private static int UnixOpenCloseOnExec => OperatingSystem.IsMacOS() ? 0x1000000 : 0x80000;
    private static int UnixOpenNonBlocking => OperatingSystem.IsMacOS() ? 0x4 : 0x800;

    private enum FileInfoByHandleClass
    {
        FileRenameInfo = 3,
        FileDispositionInfo = 4,
        FileAttributeTagInfo = 9,
        FileIdInfo = 18
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existingFileName, string newFileName, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string pathName, IntPtr securityAttributes);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, FileInfoByHandleClass fileInformationClass, byte[] fileInformation, uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle file);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, FileInfoByHandleClass fileInformationClass, out CapabilityCatalogFileAttributeTagInfo fileInformation, uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, FileInfoByHandleClass fileInformationClass, out CapabilityCatalogFileIdInfo fileInformation, uint bufferSize);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int open(string path, int flags, int mode);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int openat(SafeFileHandle directory, string path, int flags, int mode);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int mkdirat(SafeFileHandle directory, string path, int mode);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int renameat(SafeFileHandle oldDirectory, string oldPath, SafeFileHandle newDirectory, string newPath);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int unlinkat(SafeFileHandle directory, string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int flock(SafeFileHandle file, int operation);

    [DllImport("libc", SetLastError = true)]
    private static extern int fsync(SafeFileHandle file);

    [DllImport("libc", SetLastError = true)]
    private static extern int fchmod(SafeFileHandle file, int mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int statx(SafeFileHandle directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out CapabilityCatalogLinuxStatx information);

    [DllImport("libc", SetLastError = true)]
    private static extern int fstat(SafeFileHandle file, out CapabilityCatalogMacStat information);
}
