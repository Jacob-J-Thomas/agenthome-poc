using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EmbodySense.Tests.Support;

public sealed class CrossProcessExclusiveFileLock : IDisposable
{
    private const uint WindowsExclusiveLock = 0x00000002;
    private const uint WindowsFailImmediately = 0x00000001;
    private const int UnixExclusiveNonblockingLock = 2 | 4;
    private const int LinuxAtCurrentWorkingDirectory = -100;
    private const int LinuxAtNoAutomount = 0x800;
    private const int LinuxAtEmptyPath = 0x1000;
    private const int LinuxAtSymbolicLinkNoFollow = 0x100;
    private const uint LinuxStatxBasicStats = 0x7ff;
    private const uint LinuxRequiredIdentityMask = 0x105;
    private FileStream? _stream;

    private CrossProcessExclusiveFileLock(FileStream stream)
    {
        _stream = stream;
    }

    public static CrossProcessExclusiveFileLock Acquire(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var exactPath = OperatingSystem.IsWindows() ? path : Path.GetFullPath(path);
        var stream = Open(exactPath);
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                RequireCurrentUnixIdentity(stream, exactPath);
            }

            if (!TryAcquire(stream))
            {
                throw new IOException("The test fixture could not acquire the cross-process file lock.", new Win32Exception(Marshal.GetLastWin32Error()));
            }

            if (!OperatingSystem.IsWindows())
            {
                RequireCurrentUnixIdentity(stream, exactPath);
            }

            return new CrossProcessExclusiveFileLock(stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _stream, null)?.Dispose();
    }

    private static FileStream Open(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        }

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("Cross-process file locking is not supported on this platform.");
        }

        const int UnixReadOnly = 0;
        var descriptor = UnixOpen(path, UnixReadOnly | UnixNoFollowFlag | UnixCloseOnExecFlag | UnixNonBlockingFlag);
        if (descriptor < 0)
        {
            throw new IOException("The test fixture could not open the cross-process file lock safely.", new Win32Exception(Marshal.GetLastWin32Error()));
        }

        var handle = new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
        try
        {
            return new FileStream(handle, FileAccess.Read, 1, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static bool TryAcquire(FileStream stream)
    {
        if (OperatingSystem.IsWindows())
        {
            var overlapped = new Overlapped();
            return LockFileEx(stream.SafeFileHandle, WindowsExclusiveLock | WindowsFailImmediately, 0, 1, 0, ref overlapped);
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return Flock(stream.SafeFileHandle.DangerousGetHandle().ToInt32(), UnixExclusiveNonblockingLock) == 0;
        }

        throw new PlatformNotSupportedException("Cross-process file locking is not supported on this platform.");
    }

    private static void RequireCurrentUnixIdentity(FileStream stream, string path)
    {
        var handleIdentity = InspectUnix(path, stream.SafeFileHandle.DangerousGetHandle().ToInt32(), followPath: true);
        if (handleIdentity != InspectUnix(path, -1, followPath: false))
        {
            throw new InvalidOperationException("The test fixture lock path no longer resolves to the opened single-link regular file.");
        }
    }

    private static (ulong Device, ulong File, ulong Links) InspectUnix(string path, int descriptor, bool followPath)
    {
        return OperatingSystem.IsLinux()
            ? InspectLinux(path, descriptor, followPath)
            : InspectMac(path, descriptor, followPath);
    }

    private static (ulong Device, ulong File, ulong Links) InspectLinux(string path, int descriptor, bool followPath)
    {
        var directoryDescriptor = followPath ? descriptor : LinuxAtCurrentWorkingDirectory;
        var inspectedPath = followPath ? string.Empty : path;
        var flags = LinuxAtNoAutomount | (followPath ? LinuxAtEmptyPath : LinuxAtSymbolicLinkNoFollow);
        if (Statx(directoryDescriptor, inspectedPath, flags, LinuxStatxBasicStats, out var information) != 0)
        {
            throw new IOException("The test fixture could not inspect the cross-process file lock safely.", new Win32Exception(Marshal.GetLastWin32Error()));
        }

        if ((information.Mask & LinuxRequiredIdentityMask) != LinuxRequiredIdentityMask)
        {
            throw new IOException("Linux omitted file identity fields required by the cross-process test lock.");
        }

        ValidateUnixFile(path, information.Mode, information.LinkCount);
        var device = ((ulong)information.DeviceMajor << 32) | information.DeviceMinor;
        return (device, information.Inode, information.LinkCount);
    }

    private static (ulong Device, ulong File, ulong Links) InspectMac(string path, int descriptor, bool followPath)
    {
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            Marshal.Copy(new byte[256], 0, buffer, 256);
            var result = followPath ? Fstat(descriptor, buffer) : Lstat(path, buffer);
            if (result != 0)
            {
                throw new IOException("The test fixture could not inspect the cross-process file lock safely.", new Win32Exception(Marshal.GetLastWin32Error()));
            }

            var device = unchecked((uint)Marshal.ReadInt32(buffer, 0));
            var mode = unchecked((ushort)Marshal.ReadInt16(buffer, 4));
            var links = unchecked((ushort)Marshal.ReadInt16(buffer, 6));
            var file = unchecked((ulong)Marshal.ReadInt64(buffer, 8));
            ValidateUnixFile(path, mode, links);
            return (device, file, links);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void ValidateUnixFile(string path, uint mode, ulong links)
    {
        const uint UnixFileTypeMask = 0xF000;
        const uint UnixRegularFile = 0x8000;
        if ((mode & UnixFileTypeMask) != UnixRegularFile || links != 1)
        {
            throw new InvalidOperationException($"The test fixture refuses a non-regular or multiply linked lock file: `{path}`.");
        }
    }

    private static int UnixNoFollowFlag => OperatingSystem.IsMacOS() ? 0x100 : RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0x8000 : 0x20000;

    private static int UnixCloseOnExecFlag => OperatingSystem.IsMacOS() ? 0x1000000 : 0x80000;

    private static int UnixNonBlockingFlag => OperatingSystem.IsMacOS() ? 0x4 : 0x800;

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatx
    {
        [FieldOffset(0)]
        internal uint Mask;

        [FieldOffset(16)]
        internal uint LinkCount;

        [FieldOffset(28)]
        internal ushort Mode;

        [FieldOffset(32)]
        internal ulong Inode;

        [FieldOffset(136)]
        internal uint DeviceMajor;

        [FieldOffset(140)]
        internal uint DeviceMinor;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool LockFileEx(
        SafeFileHandle fileHandle,
        uint flags,
        uint reserved,
        uint numberOfBytesToLockLow,
        uint numberOfBytesToLockHigh,
        ref Overlapped overlapped);

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(int fileDescriptor, int operation);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int UnixOpen(string path, int flags);

    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
    private static extern int Lstat(string path, IntPtr buffer);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int Fstat(int descriptor, IntPtr buffer);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directoryDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out LinuxStatx information);
}
