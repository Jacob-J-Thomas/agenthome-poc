using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EmbodySense.Core.Persistence.Loops;

/// <summary>Provides no-follow regular-file opens for default-conversation turn persistence.</summary>
internal static class DefaultConversationTurnNativeFileSystem
{
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeReparsePoint = 0x400;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileShareRead = 0x1;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint OpenAlways = 4;
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

    public static FileStream? TryAcquireExclusiveLease(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var handle = OpenWindowsRegularFile(path, readWrite: true, exclusive: true, create: true, returnNullWhenContended: true);
            return handle is null ? null : new FileStream(handle, FileAccess.ReadWrite, 1, isAsync: false);
        }

        return TryAcquireUnixExclusiveLease(path);
    }

    [ExcludeFromCodeCoverage(Justification = "This OS-native shim is covered through public cross-process store behavior; its alternate platform is unreachable in any one coverage run.")]
    private static FileStream? TryAcquireUnixExclusiveLease(string path)
    {
        var unixHandle = OpenUnixRegularFile(path, readWrite: true, create: true);
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

        throw NativeIOException("The default-conversation active-set lease could not be acquired", error);
    }

    public static FileStream OpenRegularRead(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var handle = OpenWindowsRegularFile(path, readWrite: false, exclusive: false, create: false, returnNullWhenContended: false);
            return new FileStream(handle ?? throw new FileNotFoundException("The default-conversation turn artifact was not found.", path), FileAccess.Read, 4_096, isAsync: false);
        }

        var unixHandle = OpenUnixRegularFile(path, readWrite: false, create: false);
        return new FileStream(unixHandle, FileAccess.Read, 4_096, isAsync: false);
    }

    [ExcludeFromCodeCoverage(Justification = "This OS-native shim is covered through public store behavior on Windows; Windows is unreachable in Unix coverage runs.")]
    private static SafeFileHandle? OpenWindowsRegularFile(string path, bool readWrite, bool exclusive, bool create, bool returnNullWhenContended)
    {
        var desiredAccess = readWrite ? GenericRead | GenericWrite : GenericRead;
        var shareMode = exclusive ? 0U : FileShareRead;
        var disposition = create ? OpenAlways : OpenExisting;
        var handle = CreateFile(path, desiredAccess, shareMode, IntPtr.Zero, disposition, FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            if (!create && error is ErrorFileNotFound or ErrorPathNotFound)
            {
                return null;
            }

            if (returnNullWhenContended && error is ErrorSharingViolation or ErrorLockViolation)
            {
                return null;
            }

            throw NativeIOException($"Default-conversation persistence file `{path}` could not be opened safely", error);
        }

        if (!GetFileInformationByHandleEx(handle, FileInfoByHandleClass.FileAttributeTagInfo, out WindowsFileAttributeTagInfo information, (uint)Marshal.SizeOf<WindowsFileAttributeTagInfo>()))
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw NativeIOException($"Default-conversation persistence file metadata for `{path}` could not be read", error);
        }

        if ((information.FileAttributes & (FileAttributeDirectory | FileAttributeReparsePoint)) != 0)
        {
            handle.Dispose();
            throw new IOException($"Default-conversation persistence file `{path}` is not a regular file.");
        }

        return handle;
    }

    [ExcludeFromCodeCoverage(Justification = "This OS-native shim is covered through public store behavior on Unix; Unix is unreachable in Windows coverage runs.")]
    private static SafeFileHandle OpenUnixRegularFile(string path, bool readWrite, bool create)
    {
        var flags = readWrite ? UnixOpenReadWrite : UnixOpenReadOnly;
        flags |= UnixOpenNoFollow | UnixOpenCloseOnExec | UnixOpenNonBlocking;
        if (create)
        {
            flags |= UnixOpenCreate;
        }

        var descriptor = open(path, flags, PermissionUserReadWrite);
        if (descriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (!create && error == ErrorFileNotFound)
            {
                throw new FileNotFoundException("The default-conversation turn artifact was not found.", path);
            }

            throw NativeIOException($"Default-conversation persistence file `{path}` could not be opened without following links", error);
        }

        var handle = new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
        try
        {
            RequireUnixRegularFile(handle, path);
            if (create && fchmod(handle, PermissionUserReadWrite) != 0)
            {
                throw NativeIOException($"Default-conversation persistence file permissions for `{path}` could not be restricted", Marshal.GetLastPInvokeError());
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    [ExcludeFromCodeCoverage(Justification = "This OS-native shim is covered through public FIFO and symbolic-link store behavior; its alternate Unix ABI is unreachable in any one coverage run.")]
    private static void RequireUnixRegularFile(SafeFileHandle handle, string path)
    {
        ushort mode;
        if (OperatingSystem.IsLinux())
        {
            if (statx(handle, string.Empty, AtEmptyPath, StatxMode, out LinuxStatx information) != 0)
            {
                throw NativeIOException($"Default-conversation persistence file metadata for `{path}` could not be read", Marshal.GetLastPInvokeError());
            }

            if ((information.Mask & StatxMode) == 0)
            {
                throw new IOException($"Default-conversation persistence file metadata for `{path}` omitted its filesystem entry type.");
            }

            mode = information.Mode;
        }
        else if (OperatingSystem.IsMacOS())
        {
            if (fstat(handle, out MacStat information) != 0)
            {
                throw NativeIOException($"Default-conversation persistence file metadata for `{path}` could not be read", Marshal.GetLastPInvokeError());
            }

            mode = information.Mode;
        }
        else
        {
            throw new PlatformNotSupportedException("Default-conversation regular-file validation supports Windows, Linux, and macOS.");
        }

        if ((mode & UnixFileTypeMask) != UnixRegularFile)
        {
            throw new IOException($"Default-conversation persistence file `{path}` is not a regular file.");
        }
    }

    [ExcludeFromCodeCoverage(Justification = "Native error translation is exercised only through the host-specific shims excluded above.")]
    private static IOException NativeIOException(string message, int error)
    {
        return new IOException($"{message}: {new Win32Exception(error).Message}", unchecked((int)(0x80070000U | (uint)error)));
    }

    private static int UnixOpenReadOnly => 0;
    private static int UnixOpenReadWrite => 2;
    private static int UnixOpenCreate => OperatingSystem.IsMacOS() ? 0x200 : 0x40;
    private static int UnixOpenNoFollow => OperatingSystem.IsMacOS() ? 0x100 : 0x20000;
    private static int UnixOpenCloseOnExec => OperatingSystem.IsMacOS() ? 0x1000000 : 0x80000;
    private static int UnixOpenNonBlocking => OperatingSystem.IsMacOS() ? 0x4 : 0x800;

    private enum FileInfoByHandleClass
    {
        FileAttributeTagInfo = 9
    }

    private struct WindowsFileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    private struct StatxTimestamp
    {
        public long Seconds;
        public uint Nanoseconds;
        public int Reserved;
    }

    private struct LinuxStatx
    {
        public uint Mask;
        public uint BlockSize;
        public ulong Attributes;
        public uint LinkCount;
        public uint UserId;
        public uint GroupId;
        public ushort Mode;
        public ushort Spare0;
        public ulong Inode;
        public ulong Size;
        public ulong Blocks;
        public ulong AttributesMask;
        public StatxTimestamp AccessTime;
        public StatxTimestamp BirthTime;
        public StatxTimestamp ChangeTime;
        public StatxTimestamp ModificationTime;
        public uint DeviceIdMajor;
        public uint DeviceIdMinor;
        public uint DeviceMajor;
        public uint DeviceMinor;
        public ulong MountId;
        public uint DirectIoMemoryAlignment;
        public uint DirectIoOffsetAlignment;
        public ulong Spare1;
        public ulong Spare2;
        public ulong Spare3;
        public ulong Spare4;
        public ulong Spare5;
        public ulong Spare6;
        public ulong Spare7;
        public ulong Spare8;
        public ulong Spare9;
        public ulong Spare10;
        public ulong Spare11;
        public ulong Spare12;
    }

    private struct MacTimespec
    {
        public long Seconds;
        public long Nanoseconds;
    }

    private struct MacStat
    {
        public uint Device;
        public ushort Mode;
        public ushort LinkCount;
        public ulong Inode;
        public uint UserId;
        public uint GroupId;
        public uint RawDevice;
        public MacTimespec AccessTime;
        public MacTimespec ModificationTime;
        public MacTimespec ChangeTime;
        public MacTimespec BirthTime;
        public long Size;
        public long Blocks;
        public int BlockSize;
        public uint Flags;
        public uint Generation;
        public int Spare;
        public long Reserved1;
        public long Reserved2;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, FileInfoByHandleClass fileInformationClass, out WindowsFileAttributeTagInfo fileInformation, uint bufferSize);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int open(string path, int flags, int mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int flock(SafeFileHandle file, int operation);

    [DllImport("libc", SetLastError = true)]
    private static extern int fchmod(SafeFileHandle file, int mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int statx(SafeFileHandle file, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out LinuxStatx information);

    [DllImport("libc", SetLastError = true)]
    private static extern int fstat(SafeFileHandle file, out MacStat information);
}
