using System.Runtime.InteropServices;
using System.Text;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Persistence.Loops.Models;
using Microsoft.Win32.SafeHandles;

namespace EmbodySense.Core.Persistence.Loops;

/// <summary>Provides the narrow retained-directory native publication surface for canonical custom-loop run artifacts.</summary>
internal static class CustomLoopRunNativeFileSystem
{
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeReparsePoint = 0x400;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagWriteThrough = 0x80000000;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint FileShareDelete = 0x4;
    private const uint FileListDirectory = 0x00000001;
    private const uint FileTraverse = 0x00000020;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint SynchronizeAccess = 0x00100000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint ObjectCaseInsensitive = 0x00000040;
    private const uint NtFileOpen = 1;
    private const uint NtFileCreate = 2;
    private const uint NtFileOpenIf = 3;
    private const uint NtFileDirectory = 0x00000001;
    private const uint NtFileNonDirectory = 0x00000040;
    private const uint NtFileSynchronousIoNonAlert = 0x00000020;
    private const uint NtFileOpenReparsePoint = 0x00200000;
    private const uint NtFileWriteThrough = 0x00000002;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint OpenExisting = 3;
    private const int FileRenameInformationEx = 65;
    private const uint FileRenameReplaceIfExists = 0x00000001;
    private const uint FileRenamePosixSemantics = 0x00000002;
    private const int FileDispositionInformation = 4;
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorUnableToRemoveReplaced = 1175;
    private const int MacFullFsync = 51;
    private const int UnixAlreadyExists = 17;
    private const long NtFileCreated = 2;
    private const int AtEmptyPath = 0x1000;
    private const uint StatxBasicStats = 0x7ff;
    private const ushort UnixFileTypeMask = 0xF000;
    private const ushort UnixDirectory = 0x4000;
    private const ushort UnixRegularFile = 0x8000;

    public static SafeFileHandle OpenParentDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (OperatingSystem.IsWindows())
        {
            var handle = CreateFile(directory, FileListDirectory | FileTraverse | FileReadAttributes | SynchronizeAccess, FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint | FileFlagWriteThrough, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw WindowsError(error);
            }

            try
            {
                RequireWindowsDirectory(handle);
                EnsureSupportedWindowsVolume(handle, directory);
                return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("Canonical custom-loop publication requires Windows, Linux, or macOS directory durability support.");
        }

        var descriptor = open(directory, UnixOpenReadOnly | UnixOpenDirectory | UnixOpenNoFollow | UnixOpenCloseOnExec, 0);
        if (descriptor < 0)
        {
            throw PosixError(Marshal.GetLastPInvokeError());
        }

        return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
    }

    public static SafeFileHandle OpenRegularFile(SafeFileHandle parent, string name)
    {
        ArgumentNullException.ThrowIfNull(parent);
        EnsureSimpleName(name);
        if (OperatingSystem.IsWindows())
        {
            var handle = OpenWindowsRelative(parent, name, GenericRead | DeleteAccess | FileReadAttributes | SynchronizeAccess, FileShareRead | FileShareWrite | FileShareDelete, NtFileOpen, NtFileNonDirectory | NtFileSynchronousIoNonAlert | NtFileOpenReparsePoint, returnNullWhenMissing: false, out _) ?? throw new IOException("Canonical run artifact is unavailable for retained-parent publication.");
            try
            {
                RequireWindowsRegularFile(handle);
                return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        var descriptor = openat(parent, name, UnixOpenReadOnly | UnixOpenNoFollow | UnixOpenCloseOnExec, 0);
        if (descriptor < 0)
        {
            throw PosixError(Marshal.GetLastPInvokeError());
        }

        var unixHandle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        try
        {
            _ = GetRegularFileIdentity(unixHandle);
            return unixHandle;
        }
        catch
        {
            unixHandle.Dispose();
            throw;
        }
    }

    public static SafeFileHandle CreateStagingFile(SafeFileHandle parent, string name)
    {
        ArgumentNullException.ThrowIfNull(parent);
        EnsureSimpleName(name);
        if (OperatingSystem.IsWindows())
        {
            var handle = OpenWindowsRelative(parent, name, GenericRead | GenericWrite | DeleteAccess | FileReadAttributes | SynchronizeAccess, 0, NtFileCreate, NtFileNonDirectory | NtFileSynchronousIoNonAlert | NtFileOpenReparsePoint | NtFileWriteThrough, returnNullWhenMissing: false, out _) ?? throw new IOException("Canonical run staging artifact could not be created relative to its retained parent.");
            try
            {
                RequireWindowsRegularFile(handle);
                return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        var descriptor = openat(parent, name, UnixOpenReadWrite | UnixOpenCreate | UnixOpenExclusive | UnixOpenNoFollow | UnixOpenCloseOnExec, 0x180);
        if (descriptor < 0)
        {
            throw PosixError(Marshal.GetLastPInvokeError());
        }

        var unixHandle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        try
        {
            SetUserOnlyPermissions(unixHandle);
            _ = GetRegularFileIdentity(unixHandle);
            return unixHandle;
        }
        catch
        {
            unixHandle.Dispose();
            throw;
        }
    }

    public static void FlushStagingFile(SafeFileHandle staging)
    {
        ArgumentNullException.ThrowIfNull(staging);
        if (OperatingSystem.IsWindows())
        {
            if (!FlushFileBuffers(staging))
            {
                throw WindowsError(Marshal.GetLastPInvokeError());
            }

            return;
        }

        FlushPosixDurably(staging);
    }

    public static SafeFileHandle OpenOrCreateChildDirectory(SafeFileHandle parent, string name, out bool created)
    {
        ArgumentNullException.ThrowIfNull(parent);
        EnsureSimpleName(name);
        if (OperatingSystem.IsWindows())
        {
            var handle = OpenWindowsRelative(parent, name, GenericRead | GenericWrite | DeleteAccess | FileReadAttributes | SynchronizeAccess, FileShareRead | FileShareWrite | FileShareDelete, NtFileOpenIf, NtFileDirectory | NtFileSynchronousIoNonAlert | NtFileOpenReparsePoint | NtFileWriteThrough, returnNullWhenMissing: false, out var information) ?? throw new IOException("Canonical run directory could not be opened relative to its retained parent.");
            try
            {
                RequireWindowsDirectory(handle);
                created = information == NtFileCreated;
                return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("Canonical custom-loop publication requires Windows, Linux, or macOS directory durability support.");
        }

        if (mkdirat(parent, name, 0x1ff) == 0)
        {
            created = true;
        }
        else
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != UnixAlreadyExists)
            {
                throw PosixError(error);
            }

            created = false;
        }

        var descriptor = openat(parent, name, UnixOpenReadOnly | UnixOpenDirectory | UnixOpenNoFollow | UnixOpenCloseOnExec, 0);
        if (descriptor < 0)
        {
            throw PosixError(Marshal.GetLastPInvokeError());
        }

        var unixHandle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        try
        {
            _ = GetDirectoryIdentity(unixHandle);
            return unixHandle;
        }
        catch
        {
            unixHandle.Dispose();
            throw;
        }
    }

    public static void FlushDirectory(SafeFileHandle directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        if (OperatingSystem.IsWindows())
        {
            // Windows creates the exact retained-parent directory with NT_FILE_WRITE_THROUGH. NTFS does not expose a portable directory FlushFileBuffers barrier; canonical files are instead reopened and flushed after publication.
            return;
        }

        FlushPosixDurably(directory);
    }

    public static CustomLoopRunNativeIdentity GetRegularFileIdentity(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (OperatingSystem.IsWindows())
        {
            if (!GetFileInformationByHandle(handle, out var information))
            {
                throw WindowsError(Marshal.GetLastPInvokeError());
            }

            if ((information.FileAttributes & (FileAttributeDirectory | FileAttributeReparsePoint)) != 0 || information.NumberOfLinks != 1)
            {
                throw new IOException("Canonical run publication requires a single-link regular file.");
            }

            return new CustomLoopRunNativeIdentity(information.VolumeSerialNumber, ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
        }

        if (OperatingSystem.IsLinux())
        {
            if (statx(handle, string.Empty, AtEmptyPath, StatxBasicStats, out var information) != 0)
            {
                throw PosixError(Marshal.GetLastPInvokeError());
            }

            if ((information.Mode & UnixFileTypeMask) != UnixRegularFile || information.LinkCount != 1)
            {
                throw new IOException("Canonical run publication requires a single-link regular file.");
            }

            return new CustomLoopRunNativeIdentity(((ulong)information.DeviceMajor << 32) | information.DeviceMinor, information.Inode);
        }

        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("Canonical custom-loop publication requires Windows, Linux, or macOS identity support.");
        }

        const int MacStatBufferBytes = 256;
        var buffer = Marshal.AllocHGlobal(MacStatBufferBytes);
        try
        {
            for (var index = 0; index < MacStatBufferBytes; index++)
            {
                Marshal.WriteByte(buffer, index, 0);
            }

            if (fstat(handle, buffer) != 0)
            {
                throw PosixError(Marshal.GetLastPInvokeError());
            }

            var device = unchecked((uint)Marshal.ReadInt32(buffer, 0));
            var mode = unchecked((ushort)Marshal.ReadInt16(buffer, 4));
            var links = unchecked((ushort)Marshal.ReadInt16(buffer, 6));
            var file = unchecked((ulong)Marshal.ReadInt64(buffer, 8));
            if ((mode & UnixFileTypeMask) != UnixRegularFile || links != 1)
            {
                throw new IOException("Canonical run publication requires a single-link regular file.");
            }

            return new CustomLoopRunNativeIdentity(device, file);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static CustomLoopRunNativeIdentity GetDirectoryIdentity(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (OperatingSystem.IsWindows())
        {
            if (!GetFileInformationByHandle(handle, out var information))
            {
                throw WindowsError(Marshal.GetLastPInvokeError());
            }

            if ((information.FileAttributes & FileAttributeDirectory) == 0 || (information.FileAttributes & FileAttributeReparsePoint) != 0)
            {
                throw new IOException("Canonical run publication requires a non-reparse parent directory.");
            }

            return new CustomLoopRunNativeIdentity(information.VolumeSerialNumber, ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
        }

        if (OperatingSystem.IsLinux())
        {
            if (statx(handle, string.Empty, AtEmptyPath, StatxBasicStats, out var information) != 0)
            {
                throw PosixError(Marshal.GetLastPInvokeError());
            }

            if ((information.Mode & UnixFileTypeMask) != UnixDirectory)
            {
                throw new IOException("Canonical run publication requires a parent directory.");
            }

            return new CustomLoopRunNativeIdentity(((ulong)information.DeviceMajor << 32) | information.DeviceMinor, information.Inode);
        }

        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("Canonical custom-loop publication requires Windows, Linux, or macOS identity support.");
        }

        const int MacStatBufferBytes = 256;
        var buffer = Marshal.AllocHGlobal(MacStatBufferBytes);
        try
        {
            for (var index = 0; index < MacStatBufferBytes; index++)
            {
                Marshal.WriteByte(buffer, index, 0);
            }

            if (fstat(handle, buffer) != 0)
            {
                throw PosixError(Marshal.GetLastPInvokeError());
            }

            var device = unchecked((uint)Marshal.ReadInt32(buffer, 0));
            var mode = unchecked((ushort)Marshal.ReadInt16(buffer, 4));
            var file = unchecked((ulong)Marshal.ReadInt64(buffer, 8));
            if ((mode & UnixFileTypeMask) != UnixDirectory)
            {
                throw new IOException("Canonical run publication requires a parent directory.");
            }

            return new CustomLoopRunNativeIdentity(device, file);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static void RevalidateCanonicalParentDirectory(string directory, CustomLoopRunNativeIdentity expectedIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        using var current = OpenParentDirectory(directory);
        if (GetDirectoryIdentity(current) != expectedIdentity)
        {
            throw new IOException("Canonical run parent directory identity could not be revalidated.");
        }
    }

    public static void RenameWithinParent(SafeFileHandle staged, SafeFileHandle parent, string stagingName, string destinationName, bool overwrite)
    {
        ArgumentNullException.ThrowIfNull(staged);
        ArgumentNullException.ThrowIfNull(parent);
        EnsureSimpleName(stagingName);
        EnsureSimpleName(destinationName);
        if (OperatingSystem.IsWindows())
        {
            RenameWindowsByHandle(staged, parent, destinationName, overwrite);
            return;
        }

        var result = overwrite
            ? renameat(parent, stagingName, parent, destinationName)
            : OperatingSystem.IsLinux()
                ? renameat2(parent, stagingName, parent, destinationName, 1)
                : OperatingSystem.IsMacOS()
                    ? renameatx_np(parent, stagingName, parent, destinationName, 0x4)
                    : throw new PlatformNotSupportedException("Canonical custom-loop publication requires Windows, Linux, or macOS atomic rename support.");
        if (result != 0)
        {
            throw PosixError(Marshal.GetLastPInvokeError());
        }
    }

    public static void FlushAfterRename(SafeFileHandle parent, string destinationName)
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (OperatingSystem.IsWindows())
        {
            using var target = OpenWindowsRelative(parent, destinationName, GenericRead | GenericWrite | FileReadAttributes | SynchronizeAccess, FileShareRead | FileShareWrite | FileShareDelete, NtFileOpen, NtFileNonDirectory | NtFileSynchronousIoNonAlert | NtFileOpenReparsePoint | NtFileWriteThrough, returnNullWhenMissing: false, out _) ?? throw new IOException("Canonical run artifact is unavailable for its durability barrier.");
            RequireWindowsRegularFile(target);
            if (!FlushFileBuffers(target))
            {
                throw WindowsError(Marshal.GetLastPInvokeError());
            }

            return;
        }

        FlushPosixDurably(parent);
    }

    public static void DeleteUnpublishedStagingFile(SafeFileHandle parent, string name, SafeFileHandle expected)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(expected);
        EnsureSimpleName(name);
        if (OperatingSystem.IsWindows())
        {
            if (!SetFileInformationByHandle(expected, FileDispositionInformation, [1], 1))
            {
                throw WindowsError(Marshal.GetLastPInvokeError());
            }

            return;
        }

        using var named = OpenRegularFile(parent, name);
        if (GetRegularFileIdentity(named) != GetRegularFileIdentity(expected))
        {
            throw new IOException("Canonical run staging identity changed before cleanup.");
        }

        if (unlinkat(parent, name, 0) != 0)
        {
            throw PosixError(Marshal.GetLastPInvokeError());
        }
    }

    public static bool IsTransientWindowsContention(Exception exception)
    {
        if (!OperatingSystem.IsWindows() || exception is not IOException and not UnauthorizedAccessException)
        {
            return false;
        }

        var error = exception is CustomLoopRunNativeIOException native && native.ErrorKind == CustomLoopRunPersistenceNativeErrorKind.Win32
            ? native.ErrorCode
            : exception.HResult & 0xffff;
        return error is ErrorAccessDenied or ErrorSharingViolation or ErrorLockViolation or ErrorUnableToRemoveReplaced;
    }

    private static void EnsureSupportedWindowsVolume(SafeFileHandle parent, string directory)
    {
        var root = Path.GetPathRoot(directory) ?? throw new PlatformNotSupportedException("Canonical custom-loop publication requires a rooted fixed local NTFS volume on Windows.");
        var fileSystemName = new StringBuilder(32);
        if (!GetVolumeInformationByHandle(parent, null, 0, out _, out _, out _, fileSystemName, fileSystemName.Capacity))
        {
            throw WindowsError(Marshal.GetLastPInvokeError());
        }

        if (GetDriveType(root) != 3 || !string.Equals(fileSystemName.ToString(), "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            throw new PlatformNotSupportedException("Canonical custom-loop publication requires a fixed local NTFS volume on Windows.");
        }
    }

    private static void RequireWindowsDirectory(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw WindowsError(Marshal.GetLastPInvokeError());
        }

        if ((information.FileAttributes & FileAttributeDirectory) == 0 || (information.FileAttributes & FileAttributeReparsePoint) != 0)
        {
            throw new IOException("Canonical run publication refuses a reparse-point or non-directory parent.");
        }
    }

    private static void RequireWindowsRegularFile(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw WindowsError(Marshal.GetLastPInvokeError());
        }

        if ((information.FileAttributes & (FileAttributeDirectory | FileAttributeReparsePoint)) != 0)
        {
            throw new IOException("Canonical run publication refuses a non-regular target.");
        }
    }

    private static SafeFileHandle? OpenWindowsRelative(SafeFileHandle parent, string name, uint desiredAccess, uint shareMode, uint disposition, uint options, bool returnNullWhenMissing, out long information)
    {
        information = 0;
        var nameBuffer = Marshal.StringToHGlobalUni(name);
        var unicodeBuffer = IntPtr.Zero;
        try
        {
            var nameBytes = checked(name.Length * sizeof(char));
            var unicode = new CustomLoopRunWindowsUnicodeString { Length = checked((ushort)nameBytes), MaximumLength = checked((ushort)(nameBytes + sizeof(char))), Buffer = nameBuffer };
            unicodeBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<CustomLoopRunWindowsUnicodeString>());
            Marshal.StructureToPtr(unicode, unicodeBuffer, fDeleteOld: false);
            var attributes = new CustomLoopRunWindowsObjectAttributes { Length = Marshal.SizeOf<CustomLoopRunWindowsObjectAttributes>(), RootDirectory = parent.DangerousGetHandle(), ObjectName = unicodeBuffer, Attributes = ObjectCaseInsensitive };
            var status = NtCreateFile(out var rawHandle, desiredAccess, ref attributes, out var ioStatus, IntPtr.Zero, FileAttributeNormal, shareMode, disposition, options, IntPtr.Zero, 0);
            GC.KeepAlive(parent);
            if (status >= 0)
            {
                information = ioStatus.Information.ToInt64();
                return new SafeFileHandle(rawHandle, ownsHandle: true);
            }

            var error = unchecked((int)RtlNtStatusToDosError(status));
            if (returnNullWhenMissing && error is 2 or 3)
            {
                return null;
            }

            throw WindowsError(error);
        }
        finally
        {
            if (unicodeBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(unicodeBuffer);
            }

            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    private static void RenameWindowsByHandle(SafeFileHandle source, SafeFileHandle parent, string destinationName, bool overwrite)
    {
        var nameBytes = Encoding.Unicode.GetBytes(destinationName);
        var rootDirectoryOffset = IntPtr.Size == 8 ? 8 : 4;
        var fileNameLengthOffset = rootDirectoryOffset + IntPtr.Size;
        var fileNameOffset = fileNameLengthOffset + sizeof(uint);
        var unalignedSize = checked(fileNameOffset + nameBytes.Length + sizeof(char));
        var bufferSize = checked((unalignedSize + IntPtr.Size - 1) & -IntPtr.Size);
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            Marshal.Copy(new byte[bufferSize], 0, buffer, bufferSize);
            var flags = overwrite ? FileRenameReplaceIfExists | FileRenamePosixSemantics : 0;
            Marshal.WriteInt32(buffer, unchecked((int)flags));
            Marshal.WriteIntPtr(buffer, rootDirectoryOffset, parent.DangerousGetHandle());
            Marshal.WriteInt32(buffer, fileNameLengthOffset, nameBytes.Length);
            Marshal.Copy(nameBytes, 0, IntPtr.Add(buffer, fileNameOffset), nameBytes.Length);
            var status = NtSetInformationFile(source, out _, buffer, (uint)bufferSize, FileRenameInformationEx);
            GC.KeepAlive(parent);
            if (status < 0)
            {
                throw WindowsError(unchecked((int)RtlNtStatusToDosError(status)));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void EnsureSimpleName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name is "." or ".." || name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new IOException("Canonical run retained-parent publication requires one child filename.");
        }
    }

    private static CustomLoopRunNativeIOException WindowsError(int error)
        => new("Canonical run native filesystem operation failed.", CustomLoopRunPersistenceNativeErrorKind.Win32, error);

    private static CustomLoopRunNativeIOException PosixError(int error)
        => new("Canonical run native filesystem operation failed.", CustomLoopRunPersistenceNativeErrorKind.PosixErrno, error);

    private static void SetUserOnlyPermissions(SafeFileHandle file)
    {
        if (fchmod(file, 0x180) != 0)
        {
            throw PosixError(Marshal.GetLastPInvokeError());
        }
    }

    private static void FlushPosixDurably(SafeFileHandle handle)
    {
        if (OperatingSystem.IsMacOS())
        {
            // Apple defines F_FULLFSYNC as fsync plus a request that the drive flush its volatile cache to media.
            // Canonical publication treats an unsupported or failed request as a failed durability barrier.
            if (fcntl(handle.DangerousGetHandle().ToInt32(), MacFullFsync) != 0)
            {
                throw PosixError(Marshal.GetLastPInvokeError());
            }

            return;
        }

        if (fsync(handle) != 0)
        {
            throw PosixError(Marshal.GetLastPInvokeError());
        }
    }

    private static int UnixOpenReadOnly => 0;

    private static int UnixOpenReadWrite => 2;

    private static int UnixOpenCreate => OperatingSystem.IsMacOS() ? 0x200 : 0x40;

    private static int UnixOpenExclusive => OperatingSystem.IsMacOS() ? 0x800 : 0x80;

    private static int UnixOpenNoFollow => OperatingSystem.IsMacOS() ? 0x100 : 0x20000;

    private static int UnixOpenDirectory => OperatingSystem.IsMacOS() ? 0x100000 : 0x10000;

    private static int UnixOpenCloseOnExec => OperatingSystem.IsMacOS() ? 0x1000000 : 0x80000;

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out CustomLoopRunWindowsFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, byte[] information, uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle file);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationByHandle(SafeFileHandle rootDirectory, StringBuilder? volumeNameBuffer, int volumeNameSize, out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags, StringBuilder fileSystemNameBuffer, int fileSystemNameSize);

    [DllImport("kernel32.dll", EntryPoint = "GetDriveTypeW", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveType(string rootPathName);

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(out IntPtr fileHandle, uint desiredAccess, ref CustomLoopRunWindowsObjectAttributes objectAttributes, out CustomLoopRunWindowsIoStatusBlock ioStatusBlock, IntPtr allocationSize, uint fileAttributes, uint shareAccess, uint createDisposition, uint createOptions, IntPtr eaBuffer, uint eaLength);

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(SafeFileHandle fileHandle, out CustomLoopRunWindowsIoStatusBlock ioStatusBlock, IntPtr fileInformation, uint length, int fileInformationClass);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int open(string path, int flags, int mode);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int openat(SafeFileHandle directory, string path, int flags, int mode);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int mkdirat(SafeFileHandle directory, string path, int mode);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int renameat(SafeFileHandle oldDirectory, string oldPath, SafeFileHandle newDirectory, string newPath);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int renameat2(SafeFileHandle oldDirectory, string oldPath, SafeFileHandle newDirectory, string newPath, uint flags);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int renameatx_np(SafeFileHandle oldDirectory, string oldPath, SafeFileHandle newDirectory, string newPath, uint flags);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int unlinkat(SafeFileHandle directory, string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int fsync(SafeFileHandle file);

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int file, int command);

    [DllImport("libc", SetLastError = true)]
    private static extern int fchmod(SafeFileHandle file, int mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int statx(SafeFileHandle directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out CustomLoopRunLinuxStatx information);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int fstat(SafeFileHandle file, IntPtr buffer);
}
